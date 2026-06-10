using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace ProceduralHands {
    /// <summary>
    /// Cualquier objeto con Rigidbody + collider que una <see cref="Hand"/> puede agarrar mediante un
    /// joint físico. Gestiona el agarre/soltar/lanzar, el ajuste del rigidbody mientras se sostiene, el
    /// parentado opcional al agarrar y el resaltado por copia de malla. (Ámbito de una mano: en el caso
    /// habitual lo sostiene una sola mano a la vez.)
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DefaultExecutionOrder(-100)]
    public class Grabbable : GrabbableBase {

        [Header("Ajustes de agarre")]
        [Label("Tipo de agarre", "Sobrescribe, para este objeto, el tipo de agarre de la mano.")]
        public HandGrabType grabType = HandGrabType.Default;
        [Label("Tipo de pose de agarre", "Agarrar con la pose de agarre completa o con una pose de pinza.")]
        public HandGrabPoseType grabPoseType = HandGrabPoseType.Grab;
        [Label("Manos permitidas", "Qué mano(s) pueden agarrar este objeto.")]
        public HandType handType = HandType.both;
        [Label("Solo una mano", "Solo una mano puede sostener esto a la vez.")]
        public bool singleHandOnly = false;
        [Label("Permitir intercambio", "Los objetos de una sola mano pueden pasarse de una mano a otra al agarrar.")]
        public bool allowHeldSwapping = true;
        [Space]
        [Label("Agarre instantáneo", "Encaja el objeto en la mano al instante al agarrar (ideal para poses guardadas).")]
        public bool instantGrab = false;
        [Label("Parentar al agarrar", "Hace el objeto hijo del padre de la mano al agarrar (recomendado para la mayoría de objetos).")]
        public bool parentOnGrab = true;

        [Header("Ajustes mientras se sostiene")]
        [Label("Sin fricción al sostener", "Sustituye el material físico por uno sin fricción mientras se sostiene.")]
        public bool heldNoFriction = true;
        [Label("Drag mínimo", "Resistencia lineal mínima del objeto mientras se sostiene.", 0)]
        public float minHeldDrag = 1.5f;
        [Label("Drag angular mínimo", "Resistencia angular mínima del objeto mientras se sostiene.", 0)]
        public float minHeldAngleDrag = 3f;
        [Label("Masa mínima", "Masa mínima del objeto mientras se sostiene.", 0)]
        public float minHeldMass = 0.1f;
        [Label("Velocidad máxima", "Limita la velocidad del objeto sostenido por estabilidad.", 0)]
        public float maxHeldVelocity = 10f;
        [Label("Offset de posición", "Desplazamiento de posición respecto al punto de agarre.")]
        public Vector3 heldPositionOffset;
        [Label("Offset de rotación", "Desplazamiento de rotación (grados) respecto al punto de agarre.")]
        public Vector3 heldRotationOffset;

        [Header("Ajustes al soltar")]
        [Label("Fuerza de lanzamiento", "Multiplicador de la velocidad de lanzamiento de este objeto al soltarlo.")]
        [FormerlySerializedAs("throwMultiplyer")]
        public float throwPower = 1f;
        [Label("Fuerza de rotura del joint", "Fuerza necesaria para romper el joint de la mano. Usa Infinity para desactivarlo.")]
        public float jointBreakForce = 3500f;
        [Label("Tiempo de ignorar al soltar", "Segundos que la mano ignora este objeto tras soltarlo (reduce el clipping).", 0)]
        public float ignoreReleaseTime = 0.5f;

        [Header("Avanzado")]
        [Label("Hacer hijos agarrables", "Añade un GrabbableChild a cada collider hijo para que la mano pueda agarrarlos.")]
        public bool makeChildrenGrabbable = true;
        [Label("Peso de prioridad", "Prioridad de agarre — mayor es preferido al elegir entre objetos cercanos.", 0)]
        public float grabPriorityWeight = 1f;

        [Header("Eventos")]
        [Label("Al agarrar", "Se dispara cuando una mano agarra este objeto.")]
        public UnityHandGrabEvent onGrab = new UnityHandGrabEvent();
        [Label("Al soltar", "Se dispara cuando una mano suelta este objeto.")]
        public UnityHandGrabEvent onRelease = new UnityHandGrabEvent();
        [Label("Al apretar", "Se dispara al apretar (squeeze) mientras se sostiene.")]
        public UnityHandGrabEvent onSqueeze = new UnityHandGrabEvent();
        [Label("Al dejar de apretar", "Se dispara al dejar de apretar mientras se sostiene.")]
        public UnityHandGrabEvent onUnsqueeze = new UnityHandGrabEvent();
        [Label("Al resaltar", "Se dispara cuando la mano resalta este objeto.")]
        public UnityHandGrabEvent onHighlight = new UnityHandGrabEvent();
        [Label("Al dejar de resaltar", "Se dispara cuando la mano deja de resaltar este objeto.")]
        public UnityHandGrabEvent onUnhighlight = new UnityHandGrabEvent();
        [Label("Al romperse el joint", "Se dispara cuando el joint de la mano se rompe por fuerza.")]
        public UnityHandGrabEvent OnJointBreak = new UnityHandGrabEvent();

        // Equivalentes como delegado C# para suscriptores desde código (no se serializan ni salen en el inspector).
        public HandGrabEvent OnBeforeGrabEvent;
        public HandGrabEvent OnGrabEvent;
        public HandGrabEvent OnBeforeReleaseEvent;
        public HandGrabEvent OnReleaseEvent;
        public HandGrabEvent OnJointBreakEvent;
        public HandGrabEvent OnSqueezeEvent;
        public HandGrabEvent OnUnsqueezeEvent;
        public HandGrabEvent OnHighlightEvent;
        public HandGrabEvent OnUnhighlightEvent;

        /// <summary>Si este objeto fue soltado a la fuerza (drop) en lugar de lanzado.</summary>
        public bool wasForceReleased { get; protected internal set; }
        /// <summary>La última mano que sostuvo este objeto.</summary>
        public Hand lastHeldBy { get; protected set; }

        // Flag interno: indica si ya aplicamos los ajustes de rigidbody "sostenido" (evita aplicarlos dos veces).
        bool rigidbodyGrabbedState;

        /// <summary>Inicialización: prepara la base y, si procede, hace agarrables los colliders hijos.</summary>
        public override void Awake() {
            base.Awake();
            // Si está activado, recorremos los hijos añadiendo GrabbableChild a sus colliders.
            if (makeChildrenGrabbable)
                MakeChildrenGrabbable();
        }

        /// <summary>Al desactivarse el objeto: si alguna mano lo sostiene, forzamos que lo suelten.</summary>
        protected virtual void OnDisable() {
            // Un objeto desactivado no puede seguir "sostenido": liberamos las manos para no dejar joints colgando.
            if (heldBy.Count != 0)
                ForceHandsRelease();
        }

        /// <summary>Al destruirse: liberamos manos y limpiamos el combinador de poses.</summary>
        protected override void OnDestroy() {
            base.OnDestroy();
            // Si algo lo sostiene al destruirse, forzamos la liberación (evita referencias a un objeto muerto).
            if (heldBy.Count != 0)
                ForceHandsRelease();
            // El combinador de poses es un componente que creamos nosotros; lo destruimos para no dejar basura.
            if (poseCombiner != null)
                Destroy(poseCombiner);
        }

        //=================================================================
        //========================= AGARRE / SOLTAR =======================
        //=================================================================

        /// <summary>Indica si <paramref name="hand"/> tiene permiso para agarrar este objeto.</summary>
        public virtual bool CanGrab(Hand hand) {
            // Sin mano no hay nada que comprobar.
            if (hand == null)
                return false;
            // Debe estar habilitado, ser agarrable, y el lado de la mano debe coincidir con handType.
            return enabled && isGrabbable &&
                (handType == HandType.both                       // ambas manos permitidas
                 || (handType == HandType.left && hand.left)     // solo izquierda y la mano es izquierda
                 || (handType == HandType.right && !hand.left)); // solo derecha y la mano es derecha
        }

        /// <summary>Devuelve la pose guardada utilizable más cercana para la mano, si la hay.</summary>
        public bool GetGrabPose(Hand hand, out GrabbablePose pose) {
            // Si hay combinador de poses y esta mano puede posar, pedimos la pose más cercana.
            if (GetSavedPose(out var combiner) && combiner.CanSetPose(hand, this)) {
                pose = combiner.GetClosestPose(hand, this);
                return pose != null; // true solo si efectivamente hay una pose válida
            }
            // Sin poses guardadas/aplicables.
            pose = null;
            return false;
        }

        /// <summary>La llama la mano cuando empieza un agarre (antes de la animación de aproximación).</summary>
        protected internal virtual void OnBeforeGrab(Hand hand) {
            // Registramos la mano como "agarrando" (aún no sostiene; está en el proceso de agarre).
            beingGrabbedBy.Add(hand);
            beingGrabbed = true;
            // Avisamos a los suscriptores de código de que va a empezar el agarre.
            OnBeforeGrabEvent?.Invoke(hand, this);
            // Quitamos el resaltado: ya no estamos "apuntando", estamos agarrando.
            Unhighlight(hand);
            // Ignoramos la colisión con la mano durante la aproximación, para que la mano pueda atravesar el objeto.
            IgnoreHandCollision(hand, hand.maxGrabTime);
        }

        /// <summary>La llama la mano una vez creada la conexión del agarre.</summary>
        protected internal virtual void OnGrab(Hand hand) {
            // Ya no está "en proceso de agarre": pasa a estar sostenido.
            beingGrabbedBy.Remove(hand);
            // Aseguramos que la referencia al rigidbody es válida.
            ActivateRigidbody();

            // En modo Climb el asidero es un punto de apoyo fijo: NO tocamos su física ni lo parentamos
            // (no debe moverse con la mano; la escalada la gestiona XRI). En cualquier otro modo, agarre normal.
            if (grabPoseType != HandGrabPoseType.Climb) {
                // Aplicamos los ajustes de rigidbody de "sostenido" (drag, masa, material sin fricción, etc.).
                SetGrabbedRigidbodySettings();
                // Si procede, parentamos el objeto bajo el padre de la mano (sigue a la mano de forma estable).
                if (parentOnGrab && rootTransform != null)
                    rootTransform.parent = hand.transform.parent;
            }

            // Activamos el rastreador de colisiones y registramos la mano como dueña.
            collisionTracker.enabled = true;
            heldBy.Add(hand);

            // El joint es quien sostiene el objeto; evitamos que los colliders de la mano "peleen" con él mientras se sostiene.
            // (Se restaura tras soltar mediante la rutina "ignorar hasta que no haya contactos" de BreakHandConnection.)
            StopIgnoreRoutine(hand);
            IgnoreHand(hand, true);

            // Si estaba colocado en un place point, lo retiramos de él.
            placePoint?.Remove(this);

            // Disparamos los eventos de agarre (UnityEvent del inspector + delegado de código).
            onGrab?.Invoke(hand, this);
            OnGrabEvent?.Invoke(hand, this);
            // Reiniciamos el flag de "soltado a la fuerza" y cerramos el estado de agarre en curso.
            wasForceReleased = false;
            beingGrabbed = false;
        }

        /// <summary>La llama la mano al soltar: aplica el lanzamiento y coloca en un place point si lo hay.</summary>
        protected internal virtual void OnRelease(Hand hand) {
            // Caso normal: esta mano lo sostenía.
            if (heldBy.Contains(hand)) {
                // ¿Hay un place point que pueda aceptar este objeto? Lo comprobamos ANTES de romper la conexión.
                bool canPlace = placePoint != null && placePoint.CanPlace(this);
                // Rompemos la conexión (quita de heldBy, restaura ajustes, programa el ignorar al soltar).
                BreakHandConnection(hand);
                // Aplicamos la velocidad de lanzamiento a partir de la velocidad estimada de la mano.
                SetThrowVelocity(hand.ThrowVelocity(), hand.ThrowAngularVelocity());
                // Si encaja en un place point, lo colocamos (snap) en él.
                if (canPlace)
                    placePoint.Place(this);
                // Eventos de soltar (delegado + UnityEvent) y quitamos el resaltado.
                OnReleaseEvent?.Invoke(hand, this);
                onRelease?.Invoke(hand, this);
                Unhighlight(hand);
            }
            // Caso alternativo: la mano estaba agarrando (aproximándose) pero aún no sostenía; cancelamos esa conexión.
            else if (beingGrabbedBy.Contains(hand)) {
                hand.BreakGrabConnection();
            }
        }

        /// <summary>Quita la conexión con la mano (sin lanzamiento ni eventos) y restaura los ajustes de soltar.</summary>
        protected internal virtual void BreakHandConnection(Hand hand) {
            // Por si estaba en proceso de agarre, lo quitamos de esa lista también.
            beingGrabbedBy.Remove(hand);
            // Si la mano no estaba realmente sosteniéndolo, no hay nada más que hacer.
            if (!heldBy.Remove(hand))
                return;

            // Restauramos parent y ajustes de rigidbody si ya no lo sostiene ninguna mano.
            ResetGrabbableAfterRelease();
            // Dejamos de ignorar la colisión "permanente" que habíamos puesto al agarrar.
            if (ignoringHand.ContainsKey(hand))
                IgnoreHand(hand, false);
            // Programamos ignorar la colisión un ratito tras soltar, hasta que dejen de tocarse (reduce el clipping).
            if (gameObject.activeInHierarchy && !beingDestroyed)
                IgnoreHandCollisionUntilNone(hand, ignoreReleaseTime);
            // Si ya no queda ninguna mano en proceso de agarre, cerramos el estado de "siendo agarrado".
            if (beingGrabbedBy.Count == 0)
                beingGrabbed = false;
            // Recordamos cuál fue la última mano que lo sostuvo.
            lastHeldBy = hand;
        }

        /// <summary>Ordena a todas las manos que lo sostienen que lo suelten (con lanzamiento).</summary>
        public virtual void HandsRelease() {
            // Recorremos hacia atrás porque Release() modifica la lista heldBy.
            for (int i = heldBy.Count - 1; i >= 0; i--)
                heldBy[i].Release();
        }

        /// <summary>Fuerza la liberación de todas las manos sin aplicar lanzamiento.</summary>
        public override void ForceHandsRelease() {
            // Primero cancelamos las manos que estaban en proceso de agarre.
            for (int i = beingGrabbedBy.Count - 1; i >= 0; i--)
                beingGrabbedBy[i].BreakGrabConnection();
            // Luego soltamos a la fuerza las manos que lo sostienen (hacia atrás: ForceHandRelease modifica la lista).
            for (int i = heldBy.Count - 1; i >= 0; i--) {
                wasForceReleased = true;
                ForceHandRelease(heldBy[i]);
            }
        }

        /// <summary>Fuerza la liberación de una mano concreta sin aplicar lanzamiento.</summary>
        public virtual void ForceHandRelease(Hand hand) {
            // Si esa mano lo sostiene, lo soltamos con lanzamiento a 0 (para que no salga disparado).
            if (heldBy.Contains(hand)) {
                // Guardamos el multiplicador real, lo ponemos a 0, soltamos, y lo restauramos.
                float mult = throwPower;
                throwPower = 0f;
                wasForceReleased = true;
                hand.Release();
                throwPower = mult;
            }
            // Si solo estaba agarrando (aproximándose), rompemos esa conexión sin más.
            else if (beingGrabbedBy.Contains(hand)) {
                hand.BreakGrabConnection();
            }
        }

        /// <summary>Se llama cuando el joint de la mano se rompe por fuerza.</summary>
        public virtual void OnHandJointBreak(Hand hand) {
            // Si esa mano no lo sostiene, ignoramos (el joint roto no es de este agarre).
            if (!heldBy.Contains(hand))
                return;
            // Detenemos en seco el objeto: lo despertamos y anulamos sus velocidades (evita que salga volando al romperse).
            if (body != null) {
                body.WakeUp();
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            // Disparamos los eventos de rotura (delegado + UnityEvent).
            OnJointBreakEvent?.Invoke(hand, this);
            OnJointBreak?.Invoke(hand, this);
            // Y finalmente forzamos la liberación de esa mano.
            ForceHandRelease(hand);
        }

        /// <summary>Evento de apretar (squeeze) reenviado desde la mano.</summary>
        protected internal virtual void OnSqueeze(Hand hand) {
            OnSqueezeEvent?.Invoke(hand, this);
            onSqueeze?.Invoke(hand, this);
        }

        /// <summary>Evento de dejar de apretar reenviado desde la mano.</summary>
        protected internal virtual void OnUnsqueeze(Hand hand) {
            OnUnsqueezeEvent?.Invoke(hand, this);
            onUnsqueeze?.Invoke(hand, this);
        }

        //=================================================================
        //========================= LANZAR / ESTADO =======================
        //=================================================================

        /// <summary>Aplica la velocidad de lanzamiento al objeto (solo cuando ya no lo sostiene ninguna mano).</summary>
        protected internal void SetThrowVelocity(Vector3 throwVel, Vector3 throwAngularVel) {
            // Solo lanzamos si hay rigidbody no kinemático y nadie más lo sostiene (evita lanzar algo aún agarrado).
            if (body != null && !body.isKinematic && heldBy.Count == 0) {
                // Velocidad lineal escalada por el multiplicador de lanzamiento del objeto.
                body.linearVelocity = throwVel * throwPower;
                // La velocidad angular solo se aplica si es válida (sin NaN, que rompería la física).
                if (!float.IsNaN(throwAngularVel.x) && !float.IsNaN(throwAngularVel.y) && !float.IsNaN(throwAngularVel.z))
                    body.angularVelocity = throwAngularVel;
            }
        }

        /// <summary>Velocidad lineal actual del objeto.</summary>
        public Vector3 GetVelocity() => body != null ? body.linearVelocity : Vector3.zero;

        /// <summary>Manos que sostienen este objeto ahora mismo.</summary>
        public List<Hand> GetHeldBy() => heldBy;
        /// <summary>Número de manos que sostienen este objeto.</summary>
        public virtual int HeldCount() => heldBy.Count;
        /// <summary>Si este objeto está siendo sostenido ahora mismo.</summary>
        public bool IsHeld() => heldBy.Count > 0;
        /// <summary>Si hay un agarre en curso sobre este objeto.</summary>
        public bool BeingGrabbed() => beingGrabbed;

        /// <summary>Si la mano dada sostiene este objeto.</summary>
        public bool IsHolding(Hand hand) => heldBy.Contains(hand);

        /// <summary>Reproduce vibración háptica en todas las manos que lo sostienen.</summary>
        public void PlayHapticVibration(float duration = 0.025f, float amplitude = 0.5f) {
            foreach (var hand in heldBy)
                hand.PlayHapticVibration(duration, amplitude);
        }

        /// <summary>Activa/desactiva la colisión entre este objeto y los colliders dados.</summary>
        public void IgnoreColliders(IEnumerable<Collider> colliders, bool ignore = true) {
            // Producto cartesiano: ignoramos cada par (collider propio, collider externo).
            foreach (var col in grabColliders)
                foreach (var other in colliders)
                    if (col != null && other != null)
                        Physics.IgnoreCollision(other, col, ignore);
        }

        //=================================================================
        //==================== ESTADO DEL RIGIDBODY =======================
        //=================================================================

        /// <summary>Aplica los ajustes de rigidbody adecuados mientras el objeto está sostenido.</summary>
        internal void SetGrabbedRigidbodySettings() {
            // Si ya están aplicados o no hay rigidbody, no hacemos nada.
            if (rigidbodyGrabbedState || body == null)
                return;
            rigidbodyGrabbedState = true;

            // Detección de colisión continua (evita atravesar objetos a alta velocidad); el modo depende de si es kinemático.
            body.collisionDetectionMode = body.isKinematic ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.ContinuousDynamic;
            // Sin interpolación mientras se sostiene: la posición la manda el joint, la interpolación introduciría lag visual.
            body.interpolation = RigidbodyInterpolation.None;
            // Subimos mucho las iteraciones del solver para que el joint sea rígido y estable mientras se sostiene.
            body.solverIterations = 100;
            body.solverVelocityIterations = 100;

            // Garantizamos un drag/masa mínimos (solo si el objeto no tenía un objetivo propio definido), para estabilidad.
            if (targetDrag == 0 && body.linearDamping < minHeldDrag)
                body.linearDamping = minHeldDrag;
            if (targetAngularDrag == 0 && body.angularDamping < minHeldAngleDrag)
                body.angularDamping = minHeldAngleDrag;
            if (targetMass == 0 && body.mass < minHeldMass)
                body.mass = minHeldMass;

            // Si procede, ponemos un material sin fricción para que no se "pegue" a la mano ni a otras superficies.
            if (heldNoFriction)
                SetPhysicMaterial(Hand.NoFrictionMaterial);
        }

        /// <summary>Restaura los ajustes originales del rigidbody tras dejar de sostenerlo.</summary>
        protected void ResetGrabbedRigidbodySettings() {
            // Si no estaban aplicados o no hay rigidbody, no hay nada que restaurar.
            if (!rigidbodyGrabbedState || body == null)
                return;
            // Devolvemos cada propiedad a su valor original (capturado al inicio en la base).
            body.collisionDetectionMode = detectionMode;
            body.interpolation = startInterpolation;
            body.solverIterations = Physics.defaultSolverIterations;
            body.solverVelocityIterations = Physics.defaultSolverVelocityIterations;
            body.linearDamping = targetDrag;
            body.angularDamping = targetAngularDrag;
            body.mass = targetMass;

            // Restauramos también el material físico original si lo habíamos sustituido.
            if (heldNoFriction)
                ResetPhysicsMaterial();

            // Marcamos que ya no está en estado "sostenido" y apagamos el rastreador de colisiones.
            rigidbodyGrabbedState = false;
            collisionTracker.enabled = false;
        }

        /// <summary>Restaura parent y ajustes del rigidbody una vez ninguna mano sostiene el objeto.</summary>
        protected internal void ResetGrabbableAfterRelease() {
            // Solo actuamos si no se está destruyendo y ya no lo sostiene nadie.
            if (beingDestroyed || heldBy.Count != 0)
                return;

            // Si está colocado en un place point que lo parenta, debe quedarse hijo del place point (no del padre original).
            bool stayParentedToPlacePoint = placePoint != null && placePoint.placedObject == this && placePoint.parentOnPlace;
            // En caso contrario, devolvemos el objeto a su padre original de la jerarquía.
            if (parentOnGrab && gameObject.activeInHierarchy && !stayParentedToPlacePoint)
                rootTransform.parent = originalParent;

            // Y restauramos los ajustes del rigidbody.
            ResetGrabbedRigidbodySettings();
        }

        //=================================================================
        //========================= RESALTADO =============================
        //=================================================================

        /// <summary>Muestra el resaltado de este objeto.</summary>
        protected internal virtual void Highlight(Hand hand, Material customMat = null, bool ignoreEvents = false) {
            // Si ya está resaltado, no repetimos.
            if (highlighting)
                return;
            highlighting = true;
            // Disparamos los eventos de resaltar (salvo que se pidan ignorar).
            if (!ignoreEvents) {
                onHighlight?.Invoke(hand, this);
                OnHighlightEvent?.Invoke(hand, this);
            }
            // Creamos las mallas de resaltado si no existían y las activamos.
            TryCreateHighlight(customMat, hand);
            ToggleHighlight(hand, customMat, true);
        }

        /// <summary>Oculta el resaltado de este objeto.</summary>
        protected internal virtual void Unhighlight(Hand hand, Material customMat = null, bool ignoreEvents = false) {
            // Si no estaba resaltado, no hay nada que ocultar.
            if (!highlighting)
                return;
            highlighting = false;
            // Disparamos los eventos de dejar de resaltar (salvo que se pidan ignorar).
            if (!ignoreEvents) {
                onUnhighlight?.Invoke(hand, this);
                OnUnhighlightEvent?.Invoke(hand, this);
            }
            // Desactivamos las mallas de resaltado (no las destruimos: se reutilizan).
            ToggleHighlight(hand, customMat, false);
        }

        /// <summary>Crea las mallas de resaltado para el material que corresponda, si aún no existen.</summary>
        void TryCreateHighlight(Material customMat, Hand hand) {
            // Elegimos material por prioridad: el custom de la llamada, luego el del objeto, luego el por defecto de la mano.
            Material mat = customMat != null ? customMat : (highlightMaterial != null ? highlightMaterial : hand.defaultHighlight);
            // Sin material, o si ya tenemos mallas creadas para ese material, no hacemos nada.
            if (mat == null || highlightObjs.ContainsKey(mat))
                return;
            // Reservamos la lista para ese material y construimos las mallas recursivamente.
            highlightObjs[mat] = new List<GameObject>();
            AddHighlight(transform, mat);
        }

        /// <summary>Crea recursivamente una malla de resaltado por cada MeshRenderer del objeto (y sus hijos propios).</summary>
        bool AddHighlight(Transform obj, Material mat) {
            // Si encontramos OTRO Grabbable distinto a este, paramos: esa rama pertenece a otro objeto agarrable.
            if (obj != transform && obj.TryGetComponent(out Grabbable other) && other != this)
                return false;

            // Recorremos los hijos; si alguno devuelve false (es otro grabbable), cortamos la iteración.
            for (int i = 0; i < obj.childCount; i++)
                if (!AddHighlight(obj.GetChild(i), mat))
                    break;

            // Si este transform tiene malla visible, creamos una copia ligeramente más grande con el material de resaltado.
            if (obj.TryGetComponent(out MeshRenderer meshRenderer) && obj.TryGetComponent(out MeshFilter meshFilter) && meshFilter.sharedMesh != null) {
                // Objeto hijo que contendrá la malla de resaltado.
                var highlightObj = new GameObject("PH_Highlight");
                highlightObj.transform.SetParent(obj, false);
                // Escala 1.001 → la copia envuelve por fuera a la malla original (efecto contorno).
                highlightObj.transform.localScale = Vector3.one * 1.001f;
                // Reutilizamos la misma malla compartida del original.
                highlightObj.AddComponent<MeshFilter>().sharedMesh = meshFilter.sharedMesh;
                var renderer = highlightObj.AddComponent<MeshRenderer>();
                // Asignamos el material de resaltado a TODOS los submateriales del original.
                var mats = new Material[meshRenderer.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++)
                    mats[i] = mat;
                renderer.sharedMaterials = mats;
                // Guardamos la malla creada para poder activarla/desactivarla/destruirla luego.
                highlightObjs[mat].Add(highlightObj);
            }
            return true;
        }

        /// <summary>Activa o desactiva las mallas de resaltado del material que corresponda.</summary>
        void ToggleHighlight(Hand hand, Material customMat, bool enable) {
            // Mismo criterio de elección de material que al crearlas.
            Material mat = customMat != null ? customMat : (highlightMaterial != null ? highlightMaterial : hand.defaultHighlight);
            // Si tenemos mallas para ese material, las activamos/desactivamos todas.
            if (mat != null && highlightObjs.TryGetValue(mat, out var list))
                foreach (var go in list)
                    if (go != null)
                        go.SetActive(enable);
        }

        //=================================================================
        //========================= HIJOS =================================
        //=================================================================

        /// <summary>Añade un GrabbableChild a cada collider hijo para que la mano pueda agarrarlos como si fueran este objeto.</summary>
        void MakeChildrenGrabbable() {
            AddRecursive(transform);

            // Función local recursiva que recorre la jerarquía hacia abajo.
            void AddRecursive(Transform obj) {
                for (int i = 0; i < obj.childCount; i++) {
                    var child = obj.GetChild(i);
                    // Saltamos hijos que ya son agarrables por sí mismos (Grabbable / GrabbableChild / PlacePoint).
                    bool isOwnComponent = child.TryGetComponent(out Grabbable _)
                        || child.TryGetComponent(out GrabbableChild _)
                        || child.TryGetComponent(out PlacePoint _);
                    // Si no lo es y tiene un collider sólido (no trigger), le añadimos GrabbableChild apuntando a este Grabbable.
                    if (!isOwnComponent && child.TryGetComponent(out Collider col) && !col.isTrigger)
                        child.gameObject.AddComponent<GrabbableChild>().grabParent = this;

                    // Seguimos bajando, salvo que el hijo sea ya un Grabbable (ese gestiona su propia rama).
                    if (!child.TryGetComponent(out Grabbable _))
                        AddRecursive(child);
                }
            }
        }
    }
}
