using System.Collections;
using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Componente principal de la mano. Coordina el agarre (encontrar un objetivo, animar la
    /// aproximación, auto-posar los dedos y crear el joint físico), la soltada/lanzamiento y el input
    /// de grip que mueve los dedos. El movimiento, el posado y el resaltado se delegan en
    /// <see cref="HandFollower"/>, <see cref="HandAnimator"/> y <see cref="GrabbableHighlighter"/>.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public class Hand : HandBase {

        [Header("Resaltado")]
        [Label("Usar resaltado", "Activa el raycasting para resaltar grabbables apuntados.")]
        public bool usingHighlight = true;
        [Label("Capas de resaltado", "Capas que se comprueban para resaltar/agarrar. Por defecto, la capa Grabbable.")]
        public LayerMask highlightLayers;
        [Label("Material de resaltado por defecto", "Material de resaltado opcional para grabbables que no tengan el suyo.")]
        public Material defaultHighlight;

        [Header("Agarre")]
        [Label("Sin fricción en la mano", "Aplica un material físico sin fricción a los colliders de la mano al iniciar.")]
        public bool noHandFriction = true;
        [Label("Capas ignoradas al agarrar", "Capas completamente ignoradas por las comprobaciones de agarre/resaltado.")]
        public LayerMask ignoreGrabCheckLayers;
        [Label("Tipo de agarre", "Comportamiento de agarre por defecto (cada grabbable puede sobrescribirlo).")]
        public GrabType grabType = GrabType.HandToGrabbable;
        [Label("Curva de agarre", "Curva que da forma a la animación de agarre a lo largo de 0..1.")]
        public AnimationCurve grabCurve = AnimationCurve.Linear(0, 0, 1, 1);
        [Label("Tiempo mínimo de agarre", "Duración mínima de la animación de agarre (objetos cercanos).", 0f)]
        public float minGrabTime = 0.04f;
        [Label("Tiempo máximo de agarre", "Duración máxima de la animación de agarre (objetos lejanos).", 0f)]
        public float maxGrabTime = 0.33f;
        [Label("Amplificador por velocidad de mano", "Acelera el agarre según la velocidad del mando (0 lo desactiva).", 0f)]
        public float velocityGrabHandAmplifier = 600f;
        [Label("Amplificador por velocidad del objeto", "Acelera el agarre según la velocidad del objeto (0 lo desactiva).", 0f)]
        public float velocityGrabObjectAmplifier = 100f;
        [Label("Punto de mano abierta", "Punto del agarre (0..1) en el que la mano ha llegado a su pose abierta.", 0f, 1f)]
        public float grabOpenHandPoint = 0.5f;
        [Label("Índice de pose", "Las poses personalizadas deben compartir este índice con la mano para poder usarse.")]
        public int poseIndex = 0;

        [Label("Mano a copiar", "Mano de la que esta copia datos de pose en el editor (herramienta de espejado).")]
        public Hand copyFromHand;

        // Nombres de las capas físicas. Cámbialos aquí si tu proyecto usa otros nombres.
        public static string grabbableLayerNameDefault = "Grabbable";
        public static string grabbingLayerName = "Grabbing";
        public static string rightHandLayerName = "Hand";
        public static string leftHandLayerName = "Hand";

        // Eventos para programadores: se disparan en distintos momentos del ciclo de agarre/soltado.
        public event HandGrabEvent OnTriggerGrab;     // se pulsó agarrar (aunque no haya objetivo)
        public event HandGrabEvent OnBeforeGrabbed;   // justo antes de empezar a agarrar
        public event HandGrabEvent OnGrabbed;         // agarre completado (joint creado)
        public event HandGrabEvent OnTriggerRelease;  // se pulsó soltar
        public event HandGrabEvent OnBeforeReleased;  // justo antes de soltar
        public event HandGrabEvent OnReleased;        // soltado completado
        public event HandGrabEvent OnSqueezed;        // squeeze pulsado
        public event HandGrabEvent OnUnsqueezed;      // squeeze soltado
        public event HandGrabEvent OnForcedRelease;   // soltada forzada (sin throw)
        public event HandGrabEvent OnGrabJointBreak;  // el joint se rompió por fuerza

        public Grabbable lastHoldingObj { get; private set; }
        public Grabbable lookingAtObj => highlighter.currentHighlightTarget;
        public Vector3 lastFollowPosition => handFollow.lastFrameFollowPosition;

        float startGrabDist;                  // distancia palma↔punto de agarre al iniciar (ajusta el tiempo de agarre)
        Vector3 startHandLocalGrabPosition;   // posición de la mano en local del objeto al iniciar el agarre
        RaycastHit grabbingHit;               // hit del agarre en curso

        bool climbAnchored;          // true si el agarre actual es de tipo Climb y ancló la mano (kinemática)
        bool climbPrevKinematic;     // estado isKinematic de la mano antes de anclar, para restaurarlo al soltar
        Transform climbPrevParent;   // padre original de la mano antes de anclar (se desparenta al mundo durante el climb)

        Coroutine _grabRoutine;
        // Propiedad que, al asignar una nueva coroutine de agarre mientras hay otra en curso, cancela la anterior.
        Coroutine grabRoutine {
            get => _grabRoutine;
            set {
                if (value != null && _grabRoutine != null) {
                    StopCoroutine(_grabRoutine);
                    // Anulamos velocidades del objeto y rompemos la conexión a medio hacer.
                    if (holdingObj != null && holdingObj.body != null) {
                        holdingObj.body.linearVelocity = Vector3.zero;
                        holdingObj.body.angularVelocity = Vector3.zero;
                        holdingObj.beingGrabbed = false;
                    }
                    BreakGrabConnection();
                }
                _grabRoutine = value;
            }
        }

        static PhysicsMaterial _noFrictionMaterial;
        /// <summary>Material físico sin fricción compartido, creado por código (sin depender de Resources).</summary>
        public static PhysicsMaterial NoFrictionMaterial {
            get {
                if (_noFrictionMaterial == null) {
                    _noFrictionMaterial = new PhysicsMaterial("PH_NoFriction") {
                        dynamicFriction = 0f,
                        staticFriction = 0f,
                        bounciness = 0f,
                        frictionCombine = PhysicsMaterialCombine.Minimum,
                        bounceCombine = PhysicsMaterialCombine.Minimum
                    };
                }
                return _noFrictionMaterial;
            }
        }

        protected override void Awake() {
            // Ponemos toda la jerarquía de la mano en su capa (Hand). NameToLayer devuelve -1 si no existe.
            int handLayer = LayerMask.NameToLayer(left ? leftHandLayerName : rightHandLayerName);
            if (handLayer >= 0)
                SetLayerRecursive(transform, handLayer);

            // Si no se configuraron capas de resaltado, usamos por defecto la capa Grabbable.
            if (highlightLayers.value == 0) {
                int grabbable = LayerMask.NameToLayer(grabbableLayerNameDefault);
                if (grabbable >= 0)
                    highlightLayers = 1 << grabbable;
            }

            // Garantizamos una curva de agarre válida.
            if (grabCurve == null || grabCurve.keys.Length == 0)
                grabCurve = AnimationCurve.Linear(0, 0, 1, 1);

            base.Awake();
        }

        protected virtual void Start() {
            // Aplicamos el material sin fricción a los colliders de la mano (para que no "se enganche" al deslizar).
            if (noHandFriction)
                foreach (var col in handColliders)
                    col.material = NoFrictionMaterial;
        }

        //=================================================================
        //=================== INTERACCIÓN PRINCIPAL =======================
        //=================================================================

        /// <summary>Indica si esta mano puede agarrar ahora mismo <paramref name="grab"/>.</summary>
        public bool CanGrab(Grabbable grab) {
            if (grab == null)
                return false;
            // No se puede si es de una sola mano, ya está sostenido y no permite intercambio.
            bool cantSwap = grab.IsHeld() && grab.singleHandOnly && !grab.allowHeldSwapping;
            return !IsGrabbing() && !cantSwap && grab.CanGrab(this);
        }

        /// <summary>Agarra el objeto resaltado frente a la palma (lo llama el enlace del mando).</summary>
        public virtual void Grab() {
            OnTriggerGrab?.Invoke(this, null);
            // Si ya estamos agarrando o sosteniendo, no hacemos nada.
            if (grabbing || holdingObj != null)
                return;

            // Si ya hay un objetivo resaltado, lo agarramos directamente.
            if (usingHighlight && highlighter.currentHighlightTarget != null) {
                var type = GetGrabType(highlighter.currentHighlightTarget);
                grabRoutine = StartCoroutine(GrabObject(highlighter.GetHighlightHit(), highlighter.currentHighlightTarget, type));
            }
            // Si no, forzamos una comprobación de resaltado ahora mismo y agarramos lo que aparezca.
            else {
                highlighter.UpdateHighlight(true, true);
                if (highlighter.currentHighlightTarget != null) {
                    var type = GetGrabType(highlighter.currentHighlightTarget);
                    grabRoutine = StartCoroutine(GrabObject(highlighter.GetHighlightHit(), highlighter.currentHighlightTarget, type));
                }
                highlighter.ClearHighlights();
            }
        }

        /// <summary>Agarra un grabbable concreto a partir de un hit conocido.</summary>
        public virtual void Grab(RaycastHit hit, Grabbable grab, GrabType grabType = GrabType.InstantGrab) {
            // El objeto debe ser "libre" (no cinemático y sin constraints) para poder agarrarlo.
            bool objectFree = grab.body != null && !grab.body.isKinematic && grab.body.constraints == RigidbodyConstraints.None;
            if (!grabbing && holdingObj == null && CanGrab(grab) && objectFree)
                grabRoutine = StartCoroutine(GrabObject(hit, grab, grabType));
        }

        /// <summary>Fuerza el agarre de <paramref name="grab"/>, instanciando una copia si es una referencia a prefab.</summary>
        public virtual void ForceGrab(Grabbable grab, bool createCopy = false) {
            if (grab == null)
                return;
            // Si nos pasan un prefab (no está en la escena) o se pide copia, instanciamos uno.
            if (createCopy || !grab.gameObject.scene.IsValid())
                grab = Instantiate(grab);

            // Calculamos el mejor punto de agarre y lo agarramos al instante.
            if (!grabbing && holdingObj == null && CanGrab(grab) && GetClosestGrabbableHit(grab, out var hit))
                Grab(hit, grab, GrabType.InstantGrab);
        }

        /// <summary>Suelta el objeto sostenido aplicando velocidad de lanzamiento (lo llama el enlace del mando).</summary>
        public virtual void Release() {
            OnTriggerRelease?.Invoke(this, null);

            if (holdingObj != null) {
                OnBeforeReleased?.Invoke(this, holdingObj);
                // OnRelease del grabbable aplica el throw, dispara eventos y coloca en un PlacePoint si procede.
                holdingObj.OnRelease(this);
                handFollow.ignoreMoveFrame = true;
            }
            BreakGrabConnection();
        }

        /// <summary>Soltada forzada sin aplicar throw (p. ej. perder el agarre).</summary>
        public virtual void ForceReleaseGrab() {
            if (holdingObj != null) {
                OnForcedRelease?.Invoke(this, holdingObj);
                holdingObj.ForceHandRelease(this);
            }
        }

        /// <summary>Dispara el evento de squeeze en la mano y el objeto sostenido.</summary>
        public virtual void Squeeze() {
            OnSqueezed?.Invoke(this, holdingObj);
            holdingObj?.OnSqueeze(this);
            squeezing = true;
        }

        /// <summary>Dispara el evento de unsqueeze en la mano y el objeto sostenido.</summary>
        public virtual void Unsqueeze() {
            squeezing = false;
            OnUnsqueezed?.Invoke(this, holdingObj);
            holdingObj?.OnUnsqueeze(this);
        }

        /// <summary>Fija los ejes de grip (0..1) y squeeze (0..1); normalmente lo llama el enlace del mando.</summary>
        public void SetGrip(float grip, float squeeze) {
            gripAxis = grip;
            squeezeAxis = squeeze;
        }

        /// <summary>Devuelve el eje de grip (0..1).</summary>
        public virtual float GetGripAxis() => gripAxis;
        /// <summary>Devuelve el eje de squeeze (0..1).</summary>
        public float GetSqueezeAxis() => squeezeAxis;
        /// <summary>Indica si el input de squeeze está activo.</summary>
        public bool IsSqueezing() => squeezing;
        /// <summary>Devuelve el grabbable sostenido actualmente (null si no hay).</summary>
        public Grabbable GetHeld() => holdingObj;

        /// <summary>Rompe el joint y limpia el estado de agarre/sujeción, sin lanzar.</summary>
        public virtual void BreakGrabConnection(bool callEvent = true) {
            if (holdingObj != null) {
                if (squeezing)
                    holdingObj.OnUnsqueeze(this);
                // Si estábamos a media animación de agarre, anulamos velocidades del objeto.
                if (grabbing && holdingObj.body != null && !holdingObj.body.isKinematic) {
                    holdingObj.body.linearVelocity = Vector3.zero;
                    holdingObj.body.angularVelocity = Vector3.zero;
                }
                holdingObj.BreakHandConnection(this);
                lastHoldingObj = holdingObj;
                lastReleaseTime = Time.time;
                holdingObj = null;
                if (callEvent)
                    OnReleased?.Invoke(this, lastHoldingObj);
            }
            // Si no sosteníamos nada pero había una coroutine de agarre en curso, la paramos.
            else if (_grabRoutine != null) {
                StopCoroutine(_grabRoutine);
            }

            // Desactivamos el registro de throw un instante, limpiamos la pose y cancelamos el posado.
            velocityTracker.Disable(throwVelocityExpireTime);
            currentHeldPose = null;
            _grabRoutine = null;
            handAnimator.CancelPose(0.05f);

            // Si la mano estaba anclada por escalada (modo Climb), la reintegramos al rig y la devolvemos a dinámica.
            if (climbAnchored) {
                // Volvemos a colgarla de su padre original (el rig) conservando su posición de mundo actual...
                transform.SetParent(climbPrevParent, true);
                climbPrevParent = null;
                // ...y a su estado dinámico, para que el follower vuelva a llevarla hacia el mando.
                body.isKinematic = climbPrevKinematic;
                climbAnchored = false;
            }

            // Destruimos el joint físico si quedaba.
            if (heldJoint != null) {
                Destroy(heldJoint);
                heldJoint = null;
            }
        }

        /// <summary>Crea el ConfigurableJoint que conecta el cuerpo de la mano con el del grabbable.</summary>
        public virtual void CreateJoint(Grabbable grab, float breakForce, float breakTorque) {
            if (grab.body == null)
                grab.ActivateRigidbody();

            // Creamos el joint en la mano, conectado al cuerpo del objeto, anclado en el punto de agarre.
            var joint = gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = grab.body;
            joint.anchor = Vector3.zero;
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedAnchor = grab.body.transform.InverseTransformPoint(handGrabPoint.position);
            // Bloqueamos los 6 grados de libertad: el objeto queda rígidamente fijado a la mano.
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Locked;
            // Proyección: corrige derivas para que no se separen visualmente bajo estrés físico.
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.projectionDistance = 0.01f;
            joint.projectionAngle = 1f;
            joint.enablePreprocessing = false;
            // Si es la primera mano que lo sostiene, subimos la fuerza de rotura (más estable).
            joint.breakForce = grab.HeldCount() == 0 ? breakForce + 500f : breakForce;
            joint.breakTorque = breakTorque;
            heldJoint = joint;
        }

        /// <summary>Mensaje de Unity: el joint sostenido se rompió por fuerza.</summary>
        protected virtual void OnJointBreak(float breakForce) {
            if (heldJoint != null) {
                Destroy(heldJoint);
                heldJoint = null;
            }
            if (holdingObj != null) {
                // Reducimos las velocidades para que el objeto no salga disparado al romperse el joint.
                if (holdingObj.body != null) {
                    holdingObj.body.linearVelocity /= 100f;
                    holdingObj.body.angularVelocity /= 100f;
                }
                OnGrabJointBreak?.Invoke(this, holdingObj);
                holdingObj.OnHandJointBreak(this);
            }
        }

        //=================================================================
        //======================= HELPERS DE POSADO =======================
        //=================================================================

        /// <summary>Alinea la mano a un punto de impacto y flexiona cada dedo hasta que toca el objeto.</summary>
        public void AutoPose(RaycastHit hit, Grabbable grabbable) {
            // Pasamos el grabbable a la capa "Grabbing" para que el spherecast de los dedos solo lo detecte a él.
            int grabbingLayer = LayerMask.NameToLayer(grabbingLayerName);
            grabbable.SetLayerRecursive(grabbingLayer);

            // Usamos la palma o el punto de pinza según el tipo de agarre del objeto.
            Transform poseTransform = grabbable.grabPoseType == HandGrabPoseType.Pinch && pinchPointTransform != null ? pinchPointTransform : palmTransform;
            Vector3 palmLocalPos = poseTransform.localPosition;
            Quaternion palmLocalRot = poseTransform.localRotation;
            Vector3 hitColliderPosition = hit.collider.transform.position;
            Quaternion hitColliderRotation = hit.collider.transform.rotation;
            Transform palmColliderTransform = palmCollider.transform;
            Transform handTransform = transform;

            // Activamos la caja de la palma solo durante el cálculo de penetración y la apagamos al acabar.
            palmCollider.enabled = true;
            for (int i = 0; i < 12; i++) // varias iteraciones para converger en una buena colocación
                Calculate();
            palmCollider.enabled = false;

            // Coloca la mano: la lleva al punto de agarre, la separa para no penetrar el objeto y la retira un poco.
            void Calculate() {
                Align();
                // Movemos la mano para que la palma quede en el punto de impacto.
                var grabDir = hit.point - poseTransform.position;
                handTransform.position += grabDir;
                body.position = handTransform.position;

                // Si la palma penetra el objeto, la separamos la mitad de la penetración (converge en pocos pasos).
                if (Physics.ComputePenetration(hit.collider, hitColliderPosition, hitColliderRotation,
                        palmCollider, palmColliderTransform.position, palmColliderTransform.rotation, out var dir, out var dist)) {
                    handTransform.position -= dir * dist / 2f;
                    body.position = handTransform.position;
                }

                // Retiramos un poco la mano hacia atrás para que los dedos tengan recorrido para envolver.
                handTransform.position -= poseTransform.forward * grabDir.magnitude / 3f;
                body.position = handTransform.position;
            }

            // Orienta la palma para que mire al punto de impacto sin alterar la rotación global de la mano.
            void Align() {
                palmChild.position = handTransform.position;
                palmChild.rotation = handTransform.rotation;
                poseTransform.LookAt(hit.point, poseTransform.up);
                handTransform.position = palmChild.position;
                handTransform.rotation = palmChild.rotation;
                poseTransform.localPosition = palmLocalPos;
                poseTransform.localRotation = palmLocalRot;
            }

            // Con la mano colocada, flexionamos cada dedo hasta tocar el objeto (en la capa "Grabbing").
            int mask = LayerMask.GetMask(grabbingLayerName);
            if (grabbable.grabPoseType == HandGrabPoseType.Grab) {
                foreach (var finger in fingers)
                    finger.BendFingerUntilHit(fingerBendSteps, mask);
            }
            else {
                // Pinza: intentamos primero las poses de pinza; si ese dedo no llega, usamos la flexión normal.
                foreach (var finger in fingers)
                    if (!finger.BendFingerUntilHit(fingerBendSteps, mask, FingerPoseEnum.PinchOpen, FingerPoseEnum.PinchClosed))
                        finger.BendFingerUntilHit(fingerBendSteps, mask);
            }

            // Devolvemos el grabbable a sus capas originales.
            grabbable.ResetOriginalLayers();
        }

        /// <summary>Fija el offset de agarre para que la mano vuelva suavemente al mando tras agarrar.</summary>
        public void ResetGrabOffset() {
            if (follow == null)
                return;
            // El offset es la diferencia entre dónde quedó la mano al agarrar y el OBJETIVO de seguimiento del mando,
            // incluyendo el offset fijo de seguimiento (posición + rotación) — el mismo que aplica SetMoveTo. Así, justo
            // tras agarrar, el objetivo coincide con la pose actual de la mano y NO hay salto/tirón; luego el offset
            // decae suavemente hasta el objetivo real. (Con los offsets de seguimiento a cero, esto equivale al cálculo previo.)
            grabPositionOffset = transform.position - follow.position - follow.rotation * followPositionOffset;
            grabRotationOffset = Quaternion.Inverse(follow.rotation * Quaternion.Euler(followRotationOffset)) * transform.rotation;
        }

        /// <summary>Flexiona todos los dedos hasta tocar algo en <paramref name="layermask"/>.</summary>
        public void ProceduralFingerBend(int layermask) {
            foreach (var finger in fingers)
                finger.BendFingerUntilHit(fingerBendSteps, layermask);
        }

        /// <summary>Flexiona todos los dedos hasta tocar, ignorando las capas de las manos.</summary>
        public void ProceduralFingerBend() => ProceduralFingerBend(~GetHandsLayerMask());

        [ContextMenu("Pose - Abrir mano")]
        public void OpenHand() { foreach (var f in fingers) if (f != null) f.SetFingerBend(0); }
        [ContextMenu("Pose - Cerrar mano")]
        public void CloseHand() { foreach (var f in fingers) if (f != null) f.SetFingerBend(1); }
        [ContextMenu("Pose - Relajar mano")]
        public void RelaxHand() { foreach (var f in fingers) if (f != null) f.SetFingerBend(gripOffset); }

        /// <summary>Reproduce un pulso háptico en el mando de esta mano, si lo soporta.</summary>
        public void PlayHapticVibration(float duration = 0.05f, float amplitude = 0.5f) {
            var link = left ? HandControllerLink.handLeft : HandControllerLink.handRight;
            link?.TryHapticImpulse(duration, amplitude);
        }

        //=================================================================
        //========================= INTERNO ===============================
        //=================================================================

        // Resuelve el tipo de agarre efectivo: el del objeto si lo sobrescribe, si no el de la mano.
        GrabType GetGrabType(Grabbable grabbable) {
            if (grabbable.instantGrab)
                return GrabType.InstantGrab;
            if (grabbable.grabType == HandGrabType.HandToGrabbable)
                return GrabType.HandToGrabbable;
            if (grabbable.grabType == HandGrabType.GrabbableToHand)
                return GrabType.GrabbableToHand;
            return grabType;
        }

        // Calcula la duración de la animación de agarre: más larga cuanto más lejos esté el objeto.
        internal float GetGrabTime() {
            float divider = Mathf.Clamp01(startGrabDist / reachDistance);
            return Mathf.Clamp(minGrabTime * 2f + (maxGrabTime - minGrabTime) * divider, 0, maxGrabTime);
        }

        // Lanza un rayo desde la palma hacia cada collider del grabbable y devuelve el impacto más cercano.
        bool GetClosestGrabbableHit(Grabbable grab, out RaycastHit closestHit) {
            closestHit = new RaycastHit { distance = float.MaxValue };
            bool didHit = false;
            var origin = palmTransform.position;
            foreach (var col in grab.grabColliders) {
                // Apuntamos al punto del collider más cercano a la palma (o a su centro si coinciden).
                Vector3 closestPoint = col.ClosestPoint(origin);
                Ray ray = new Ray(origin, (closestPoint - origin).normalized);
                if (ray.direction == Vector3.zero)
                    ray.direction = (col.bounds.center - origin).normalized;
                if (col.Raycast(ray, out var hit, 1000f) && hit.distance < closestHit.distance) {
                    closestHit = hit;
                    didHit = true;
                }
            }
            return didHit;
        }

        /// <summary>Aproxima y agarra <paramref name="grab"/>, animando mano y dedos, y luego crea el joint.</summary>
        protected IEnumerator GrabObject(RaycastHit hit, Grabbable grab, GrabType grabType) {
            // Comprobación final por si dejó de ser agarrable entre el disparo y aquí.
            if (!CanGrab(grab))
                yield break;

            // Marcamos estado de agarre y guardamos referencias/posiciones iniciales.
            grabbing = true;
            currentHeldPose = null;
            holdingObj = grab;
            var startHoldingObj = holdingObj;

            // Modo Climb: la mano se posará sobre el asidero pero sin joint ni física; al final se anclará (kinemática).
            bool isClimb = grab.grabPoseType == HandGrabPoseType.Climb;

            var startHandPosition = transform.position;
            var startHandRotation = transform.rotation;
            var startGrabbablePosition = grab.body != null ? grab.body.position : grab.transform.position;
            var startGrabbableRotation = grab.body != null ? grab.body.rotation : grab.transform.rotation;

            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;

            grabbingHit = hit;
            handAnimator.CancelPose();

            OnBeforeGrabbed?.Invoke(this, holdingObj);
            holdingObj.OnBeforeGrab(this);

            // Colgamos el punto de agarre del objeto, en el punto/normal del impacto.
            handGrabPoint.parent = grab.rootTransform;
            handGrabPoint.position = grabbingHit.point;
            handGrabPoint.up = grabbingHit.normal;

            // Si algo se invalidó durante OnBeforeGrab, cancelamos.
            if (holdingObj == null || grabbingHit.collider == null) {
                CancelGrab();
                yield break;
            }

            bool instantGrab = holdingObj.instantGrab || grabType == GrabType.InstantGrab;
            startGrabDist = Vector3.Distance(palmTransform.position, handGrabPoint.position);
            startHandLocalGrabPosition = holdingObj.transform.InverseTransformPoint(transform.position);

            if (instantGrab)
                holdingObj.ActivateRigidbody();

            // Determinamos la pose objetivo: una pose guardada si existe; si no, auto-pose.
            // startGrabPose captura la forma de la mano ANTES de agarrar, relativa al mismo transform que la
            // pose objetivo, para que la mezcla de posición de abajo no tenga un salto.
            HandPoseData startGrabPose;
            if (holdingObj.GetGrabPose(this, out var savedPose)) {
                startGrabPose = new HandPoseData(this, savedPose.transform);
                currentHeldPose = savedPose;
                currentHeldPose.SetHandPose(this);
            }
            else {
                startGrabPose = new HandPoseData(this, holdingObj.transform);
                // Retiramos un poco la mano y auto-posamos (coloca la mano y flexiona los dedos sobre el objeto).
                transform.position -= palmTransform.forward * 0.08f;
                body.position = transform.position;
                AutoPose(grabbingHit, holdingObj);
            }

            // Guardamos la pose objetivo (de la pose guardada o de la forma resultante del auto-pose).
            if (currentHeldPose != null)
                handAnimator.targetGrabPose.CopyFromData(ref currentHeldPose.GetHandPoseData(this));
            else
                handAnimator.targetGrabPose.SavePose(this, holdingObj.transform);

            localGrabbablePoint.position = grab.rootTransform.position;
            localGrabbablePoint.rotation = grab.rootTransform.rotation;

            handAnimator.SetPose(ref handAnimator.targetGrabPose, 0f);

            if (instantGrab) {
                // Agarre instantáneo: aplicamos la pose ya, sin animación.
                if (currentHeldPose != null)
                    currentHeldPose.SetHandPose(this);
            }
            else {
                // Agarre animado: devolvemos la mano al punto de partida para animar la aproximación.
                transform.SetPositionAndRotation(startHandPosition, startHandRotation);
                body.position = startHandPosition;
                body.rotation = startHandRotation;

                float adjustedGrabTime = GetGrabTime();
                float openHandPoint = Mathf.Clamp(grabOpenHandPoint, 0.01f, 0.99f);
                Transform grabTarget = currentHeldPose != null ? currentHeldPose.transform : holdingObj.transform;

                // Pose "abierta intermedia": la pose abierta cerrada un poco según cuánto tuvo que flexionar cada dedo.
                var targetOpenPose = new HandPoseData(this);
                targetOpenPose.CopyFromData(ref handAnimator.openHandPose);
                foreach (var finger in fingers) {
                    if (finger == null || finger.fingerType == FingerEnum.none)
                        continue;
                    int idx = (int)finger.fingerType;
                    targetOpenPose.fingerPoses[idx].LerpDataTo(ref handAnimator.closeHandPose.fingerPoses[idx], finger.GetLastHitBend() / 2f);
                }

                // ¿El objeto viene a la mano (GrabbableToHand) en vez de ir la mano al objeto?
                bool grabbableToHand = grabType == GrabType.GrabbableToHand && holdingObj.parentOnGrab && holdingObj.HeldCount() == 0;

                if (grabbableToHand) {
                    // El objeto viaja hacia la mano mientras los dedos animan start→abierta→pose de agarre.
                    holdingObj.ActivateRigidbody();
                    bool useGravity = holdingObj.body != null && holdingObj.body.useGravity;
                    if (holdingObj.body != null)
                        holdingObj.body.useGravity = false; // sin gravedad mientras viaja

                    for (float i = 0; i < adjustedGrabTime; i += Time.deltaTime) {
                        if (holdingObj == null)
                            break;
                        float point = Mathf.Clamp01(i / adjustedGrabTime);

                        // Dedos: primera mitad start→abierta, segunda mitad abierta→pose de agarre.
                        if (point < openHandPoint) {
                            HandPoseData.LerpPose(ref handAnimator.handPoseDataNonAlloc, ref startGrabPose, ref targetOpenPose, grabCurve.Evaluate(point / openHandPoint));
                            handAnimator.handPoseDataNonAlloc.SetFingerPose(this);
                        }
                        else {
                            HandPoseData.LerpPose(ref handAnimator.handPoseDataNonAlloc, ref targetOpenPose, ref handAnimator.targetGrabPose, grabCurve.Evaluate((point - openHandPoint) / (1f - openHandPoint)));
                            handAnimator.handPoseDataNonAlloc.SetFingerPose(this);
                        }

                        // Movemos el objeto desde su sitio hacia la mano (anulando su velocidad para que no derive).
                        if (holdingObj.body != null && !holdingObj.body.isKinematic) {
                            float t = grabCurve.Evaluate(point / openHandPoint);
                            holdingObj.body.transform.position = Vector3.Lerp(startGrabbablePosition, localGrabbablePoint.position, t);
                            holdingObj.body.transform.rotation = Quaternion.Lerp(startGrabbableRotation, localGrabbablePoint.rotation, t);
                            holdingObj.body.position = holdingObj.body.transform.position;
                            holdingObj.body.rotation = holdingObj.body.transform.rotation;
                            holdingObj.body.linearVelocity = Vector3.zero;
                            holdingObj.body.angularVelocity = Vector3.zero;
                        }

                        // Seguimos moviendo la mano hacia el mando con física durante la animación.
                        handFollow.SetMoveTo();
                        handFollow.MoveTo(Time.fixedDeltaTime);
                        handFollow.TorqueTo(Time.fixedDeltaTime);
                        yield return new WaitForEndOfFrame();
                    }

                    // Restauramos la gravedad del objeto.
                    if (holdingObj != null && holdingObj.body != null)
                        holdingObj.body.useGravity = useGravity;
                }
                else {
                    // La mano viaja hacia el objeto.
                    for (float i = 0; i < adjustedGrabTime; i += Time.deltaTime) {
                        if (holdingObj == null)
                            break;

                        // Aceleramos la animación si el mando o el objeto se mueven rápido (sensación de agarre ágil).
                        float deltaDist = Vector3.Distance(follow.position, lastFollowPosition);
                        float maxDeltaTimeOffset = minGrabTime / adjustedGrabTime * Time.deltaTime * 5f;
                        float timeOffset = deltaDist * Time.deltaTime * velocityGrabHandAmplifier;
                        timeOffset += holdingObj.GetVelocity().magnitude * Time.deltaTime * velocityGrabObjectAmplifier * 3f;
                        i += Mathf.Clamp(timeOffset, 0, maxDeltaTimeOffset);
                        if (i >= adjustedGrabTime)
                            break;

                        float point = Mathf.Clamp01(i / adjustedGrabTime);

                        // Dedos: igual que arriba, start→abierta→pose de agarre.
                        if (point < openHandPoint) {
                            HandPoseData.LerpPose(ref handAnimator.handPoseDataNonAlloc, ref startGrabPose, ref targetOpenPose, grabCurve.Evaluate(point / openHandPoint));
                            handAnimator.handPoseDataNonAlloc.SetFingerPose(this);
                        }
                        else {
                            HandPoseData.LerpPose(ref handAnimator.handPoseDataNonAlloc, ref targetOpenPose, ref handAnimator.targetGrabPose, grabCurve.Evaluate((point - openHandPoint) / (1f - openHandPoint)));
                            handAnimator.handPoseDataNonAlloc.SetFingerPose(this);
                        }

                        // Posición: mezclamos la mano desde su sitio hacia la ubicación de la pose de agarre.
                        HandPoseData.LerpPose(ref handAnimator.handPoseDataNonAlloc, ref startGrabPose, ref handAnimator.targetGrabPose, Mathf.Clamp01(point * 1.25f));
                        handAnimator.handPoseDataNonAlloc.SetPosition(this, grabTarget);
                        body.position = transform.position;
                        body.rotation = transform.rotation;

                        // Amortiguamos un poco la velocidad del objeto para que no se escape mientras llegamos.
                        if (holdingObj.body != null && !holdingObj.body.isKinematic) {
                            holdingObj.body.angularVelocity *= 0.5f;
                            holdingObj.body.linearVelocity *= 0.95f;
                        }
                        yield return new WaitForEndOfFrame();
                    }
                }

                // Aseguramos la pose final (posición + dedos) por si la animación no llegó exactamente.
                if (holdingObj != null)
                    handAnimator.targetGrabPose.SetPose(this, grabTarget);
            }

            // Si en algún punto se perdió el objeto, cancelamos.
            if (holdingObj == null) {
                CancelGrab();
                yield break;
            }

            // Finalizamos: fijamos el punto de agarre en la pose actual de la mano y referencias del objeto.
            handGrabPoint.position = transform.position;
            handGrabPoint.rotation = transform.rotation;
            holdingObj.ActivateRigidbody();
            localGrabbablePoint.position = holdingObj.rootTransform.position;
            localGrabbablePoint.rotation = holdingObj.rootTransform.rotation;

            // Creamos el offset de retorno (salvo en agarre instantáneo parentado, donde no hace falta).
            if (!instantGrab || !holdingObj.parentOnGrab)
                ResetGrabOffset();

            // Creamos el joint físico que mantiene el objeto en la mano (en modo Climb NO hay joint: la mano se anclará abajo).
            if (!isClimb)
                CreateJoint(holdingObj, holdingObj.jointBreakForce, float.PositiveInfinity);
            handFollow.SetMoveTo();

            grabbing = false;
            startHoldingObj.beingGrabbed = false;
            lastGrabTime = Time.time;
            _grabRoutine = null;

            // Eventos de agarre completado.
            holdingObj.OnGrab(this);
            OnGrabbed?.Invoke(this, holdingObj);

            // Modo Climb: anclamos la mano (kinemática) en la pose. El follower ignora las manos kinemáticas,
            // así la mano se queda fija en el asidero mientras la escalada de XRI desplaza al jugador.
            if (isClimb) {
                climbPrevKinematic = body.isKinematic; // guardamos el estado para restaurarlo al soltar
                climbPrevParent = transform.parent;    // y el padre, porque ahora la desparentamos
                climbAnchored = true;
                body.isKinematic = true;
                // CLAVE: la mano normalmente cuelga del XR Origin. Si la escalada mueve el rig, la mano (hija) subiría
                // con él. La desparentamos al mundo (conservando su pose) para que se quede REALMENTE fija en el asidero.
                transform.SetParent(null, true);
            }
            // En agarre instantáneo parentado (no Climb), teletransportamos la mano a su sitio de seguimiento.
            else if (instantGrab && holdingObj.parentOnGrab) {
                handFollow.SetHandLocation(handFollow.moveTo.position, handFollow.moveTo.rotation);
            }

            // Función local: cancela el agarre limpiando estado y velocidades.
            void CancelGrab() {
                BreakGrabConnection();
                if (startHoldingObj != null) {
                    if (startHoldingObj.body != null) {
                        startHoldingObj.body.linearVelocity = Vector3.zero;
                        startHoldingObj.body.angularVelocity = Vector3.zero;
                    }
                    startHoldingObj.beingGrabbed = false;
                }
                grabbing = false;
                _grabRoutine = null;
            }
        }
    }
}
