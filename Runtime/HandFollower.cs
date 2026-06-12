using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Mueve la mano hacia su objetivo <see cref="HandBase.follow"/> usando fuerzas de Rigidbody:
    /// un seguimiento en velocidad (<see cref="MoveTo"/>) y otro en rotación/torque
    /// (<see cref="TorqueTo"/>), con drag y masa que se adaptan a la distancia/ángulo al objetivo.
    /// Es la versión a una mano (sin promediado entre dos manos). Un teletransporte de seguridad
    /// devuelve la mano si se queda atascada más allá de <see cref="maxFollowDistance"/>.
    /// </summary>
    [RequireComponent(typeof(Hand))]
    [DefaultExecutionOrder(0)]
    public class HandFollower : MonoBehaviour {

        Hand _hand;
        public Hand hand => _hand != null ? _hand : (_hand = GetComponent<Hand>());

        // Objetivo a seguir (cacheado desde hand.follow).
        Transform follow;

        [Header("Ajustes de Move To")]
        [Label("Distancia base de avance", "Distancia base que el punto intermedio (moveTo) avanza por frame hacia el mando.")]
        public float maxMoveToDistance = 0.1f;
        [Label("Ángulo base de giro", "Ángulo base que el punto intermedio (moveTo) gira por frame hacia el mando.")]
        public float maxMoveToAngle = 45f;
        [Label("Distancia máx. de seguimiento", "Si la mano se aleja más de esto, se teletransporta de vuelta (seguridad anti-atasco).", 0f)]
        public float maxFollowDistance = 0.5f;
        [Label("Velocidad máxima", "Velocidad lineal máxima de la mano.", 0f)]
        public float maxVelocity = 12f;

        [Header("Ajustes de posición")]
        [Label("Fuerza de seguimiento (posición)", "Fuerza con la que la mano persigue su objetivo en posición (más = más rápido pero más riesgo de jitter).", 0f)]
        public float followPositionStrength = 60f;
        [Label("Drag base", "Drag lineal base del Rigidbody de la mano.")]
        public float startDrag = 20f;
        [Label("Multiplicador de drag al acercarse", "Multiplica el drag al acercarse al objetivo, para amortiguar y no vibrar.")]
        public float dragDamper = 3f;
        [Label("Distancia del amortiguador de drag", "Distancia a la que empieza a actuar el amortiguador de drag.")]
        public float dragDamperDistance = 0.025f;
        [Label("Cambio de velocidad mínimo", "Cambio de velocidad mínimo por paso (suaviza las correcciones).")]
        public float minVelocityChange = 1f;
        [Label("Multiplicador por distancia", "Aumenta el cambio de velocidad mínimo según la distancia al objetivo.")]
        public float minVelocityDistanceMulti = 5f;

        [Header("Ajustes de rotación")]
        [Label("Fuerza de seguimiento (rotación)", "Fuerza con la que la mano persigue su objetivo en rotación.", 0f)]
        public float followRotationStrength = 100f;
        [Label("Drag angular base", "Drag angular base del Rigidbody de la mano.")]
        public float startAngularDrag = 20f;
        [Label("Multiplicador de drag angular", "Multiplica el drag angular al acercarse en ángulo, para amortiguar.")]
        public float angleDragDamper = 5f;
        [Label("Ángulo del amortiguador angular", "Ángulo (grados) al que empieza a actuar el amortiguador angular.")]
        public float angleDragDamperDistance = 3f;

        [Header("Ajustes de masa")]
        [Label("Masa mínima", "Masa mínima dinámica de la mano (cerca del objetivo).")]
        public float minMass = 0.25f;
        [Label("Masa máxima", "Masa máxima dinámica de la mano (lejos del objetivo).")]
        public float maxMass = 10f;
        [Label("Divisor de masa del objeto", "Divisor de la masa del objeto sostenido (lo hace sentir más manejable).")]
        public float heldMassDivider = 2f;
        [Label("Peso de la distancia", "Peso relativo de la distancia al calcular la masa dinámica.")]
        public float distanceMassDifference = 10f;
        [Label("Distancia de masa máxima", "Distancia a la que la masa por distancia llega al máximo.")]
        public float distanceMassMaxDistance = 0.5f;
        [Label("Peso del ángulo", "Peso relativo del ángulo al calcular la masa dinámica.")]
        public float angleMassDifference = 10f;
        [Label("Ángulo de masa máxima", "Ángulo (grados) al que la masa por ángulo llega al máximo.")]
        public float angleMassMaxAngle = 45f;

        [Header("Avanzado")]
        [Label("Pasos antes de soltar", "Pasos de física seguidos que la mano tolera más allá de la distancia máxima mientras sostiene un objeto retenido (atascado o atado), antes de soltarlo y volver al mando.")]
        public int maxDistanceReleaseFrames = 3;

        public Vector3 lastVelocity { get; protected set; }
        public Vector3 lastAngularVelocity { get; protected set; }
        public Vector3 targetMoveToPosition { get; protected set; }
        public Quaternion targetMoveToRotation { get; protected set; }

        // Si está activo, el siguiente FixedUpdate pone la velocidad a cero (tras un teletransporte).
        internal bool ignoreMoveFrame;
        // Posición/rotación del 'follow' el frame anterior (para calcular deltas en UpdateHandOffset).
        internal Vector3 lastFrameFollowPosition;
        internal Quaternion lastFrameFollowRotation;
        internal Vector3 lastFollowPosition;
        internal Vector3 lastFollowRotationEuler;
        // Historial de las últimas posiciones locales de la mano; lo usa el animador para el sway de dedos.
        internal readonly Vector3[] updatePositionTracked = new Vector3[3];

        int tryMaxDistanceCount; // pasos seguidos fuera de rango sosteniendo un objeto (paciencia antes de soltar)
        float targetMass;
        float targetHeldMass;

        Transform _moveTo;
        /// <summary>Transform objetivo intermedio hacia el que la mano aplica fuerzas.</summary>
        public Transform moveTo {
            get {
                // Se crea perezosamente, colgado del mismo padre que la mano.
                if (_moveTo == null && gameObject.activeInHierarchy) {
                    _moveTo = new GameObject("PH_HandFollowPoint").transform;
                    _moveTo.parent = transform.parent;
                }
                return _moveTo;
            }
        }

        protected virtual void Awake() {
            // Inicializamos el drag del Rigidbody con los valores base (y sin gravedad).
            hand.body.linearDamping = startDrag;
            hand.body.angularDamping = startAngularDrag;
            hand.body.useGravity = false;
        }

        protected virtual void OnDestroy() {
            // Limpiamos el transform auxiliar creado en runtime.
            if (_moveTo != null)
                Destroy(_moveTo.gameObject);
        }

        protected virtual void Update() {
            // En Update (cada frame) suavizamos el retorno del offset de agarre hacia el mando.
            UpdateHandOffset();
        }

        protected virtual void FixedUpdate() {
            // En FixedUpdate (paso de física) aplicamos las fuerzas de seguimiento.
            UpdateHandPhysicsMovement();
        }

        protected virtual void UpdateHandPhysicsMovement() {
            // Cacheamos el objetivo a seguir si cambió.
            if (follow == null || follow != hand.follow)
                follow = hand.follow;

            // Sin objetivo o con el movimiento desactivado, no hacemos nada.
            if (follow == null || !hand.enableMovement)
                return;

            // Guardamos la pose del follow de este paso (para otros cálculos).
            lastFollowPosition = follow.position;
            lastFollowRotationEuler = follow.rotation.eulerAngles;

            // Solo seguimos físicamente cuando NO estamos en pleno agarre y la mano no es cinemática.
            // (Durante el agarre, la coroutine de Hand controla el movimiento.)
            if (!hand.IsGrabbing() && !hand.body.isKinematic) {
                SetMoveTo();                 // recalcula el punto intermedio
                SetMass();                   // ajusta la masa dinámica
                MoveTo(Time.fixedDeltaTime); // aplica velocidad lineal
                TorqueTo(Time.fixedDeltaTime); // aplica velocidad angular
            }

            // Si venimos de un teletransporte, anulamos la velocidad este frame para que no salga disparada.
            if (ignoreMoveFrame) {
                hand.body.linearVelocity = Vector3.zero;
                hand.body.angularVelocity = Vector3.zero;
            }
            ignoreMoveFrame = false;

            // Desplazamos el historial de posiciones (para el sway de dedos del animador): [0] es la más reciente.
            for (int i = updatePositionTracked.Length - 1; i > 0; i--)
                updatePositionTracked[i] = updatePositionTracked[i - 1];
            updatePositionTracked[0] = transform.localPosition;
        }

        /// <summary>Suaviza el retorno del offset de agarre hacia cero para que la mano vuelva a seguir al mando.</summary>
        protected virtual void UpdateHandOffset() {
            if (follow == null || !hand.enableMovement)
                return;

            // Solo cuando no estamos agarrando: decaemos el offset que se creó al agarrar.
            if (!hand.IsGrabbing()) {
                // Cuanto más se mueve el mando este frame, más rápido devolvemos el offset (sensación natural).
                float deltaDist = Vector3.Distance(follow.position, lastFrameFollowPosition);
                float deltaRot = Quaternion.Angle(follow.rotation, lastFrameFollowRotation);
                // Si sostenemos algo, usamos la velocidad de 'gentle grab'; si no, velocidad 1 (normal).
                float returnSpeed = hand.holdingObj != null ? hand.gentleGrabSpeed : 1f;

                hand.grabPositionOffset = Vector3.Lerp(hand.grabPositionOffset, Vector3.zero, Mathf.Clamp01(Time.deltaTime * (4f + deltaDist * 60f) * returnSpeed));
                hand.grabRotationOffset = Quaternion.Lerp(hand.grabRotationOffset, Quaternion.identity, Mathf.Clamp01(Time.deltaTime * (4f + deltaRot) * returnSpeed));
            }

            // Guardamos la pose del follow para calcular el delta el próximo frame.
            lastFrameFollowPosition = follow.position;
            lastFrameFollowRotation = follow.rotation;
        }

        /// <summary>Aplica una velocidad hacia el objetivo intermedio, con amortiguación de drag según la distancia.</summary>
        internal virtual void MoveTo(float deltaTime) {
            if (followPositionStrength <= 0)
                return;

            // Punto objetivo y punto actual (si sostenemos algo y no agarramos, usamos el punto de agarre).
            var movePos = moveTo.position;
            var currentPos = hand.holdingObj != null && !hand.IsGrabbing() ? hand.handGrabPoint.position : hand.transform.position;
            var distance = Vector3.Distance(movePos, currentPos);

            // ESTABILIDAD: la fuerza efectiva se limita a 1/dt. Por encima de eso, la velocidad escrita
            // recorrería en un solo paso MÁS distancia que el error restante (fuerza × dt > 1): la mano se
            // pasaría del objetivo en cada paso de física y oscilaría alrededor de él (tembleque visible).
            float stablePositionStrength = Mathf.Min(followPositionStrength, 1f / deltaTime);
            // Velocidad deseada = (a dónde - dónde estoy) * fuerza, limitada por el clamp (del objeto si lo hay).
            var velocityClamp = hand.holdingObj != null ? hand.holdingObj.maxHeldVelocity : maxVelocity;
            Vector3 vel = (movePos - currentPos) * stablePositionStrength;
            vel.x = Mathf.Clamp(vel.x, -velocityClamp, velocityClamp);
            vel.y = Mathf.Clamp(vel.y, -velocityClamp, velocityClamp);
            vel.z = Mathf.Clamp(vel.z, -velocityClamp, velocityClamp);

            // Factor que compensa distintos timesteps (0.011111 ≈ 1/90, el timestep de referencia).
            float inverseDeltaOffset = 0.011111f / Time.fixedDeltaTime;
            Vector3 currentVelocity = hand.body.linearVelocity;

            // El drag sube al acercarse (entre startDrag*dragDamper lejos y startDrag cerca) para frenar sin vibrar.
            hand.body.linearDamping = Mathf.Lerp(startDrag * dragDamper, startDrag, distance / dragDamperDistance) * inverseDeltaOffset;

            // En vez de fijar la velocidad de golpe, nos movemos hacia ella suavemente (MoveTowards por eje).
            Vector3 towardsVel = new Vector3(
                Mathf.MoveTowards(currentVelocity.x, vel.x, minVelocityChange / 5f + Mathf.Abs(currentVelocity.x) / 1.5f),
                Mathf.MoveTowards(currentVelocity.y, vel.y, minVelocityChange / 5f + Mathf.Abs(currentVelocity.y) / 1.5f),
                Mathf.MoveTowards(currentVelocity.z, vel.z, minVelocityChange / 5f + Mathf.Abs(currentVelocity.z) / 1.5f));

            hand.body.linearVelocity = towardsVel;
            lastVelocity = towardsVel;
        }

        /// <summary>Aplica una velocidad angular que rota la mano hacia la rotación del objetivo intermedio.</summary>
        internal virtual void TorqueTo(float deltaTime) {
            if (followRotationStrength <= 0)
                return;

            // Diferencia de rotación entre el objetivo y la mano, expresada como ángulo + eje.
            var delta = moveTo.rotation * Quaternion.Inverse(hand.body.rotation);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            // Si el eje es inválido (rotación nula), salimos.
            if (float.IsInfinity(axis.x))
                return;

            // Normalizamos el ángulo a [-180,180] para girar por el camino corto.
            if (angle > 180f)
                angle -= 360f;

            // Velocidad angular = ángulo (en radianes) * fuerza, en la dirección del eje.
            float multiLinear = Mathf.Deg2Rad * angle * followRotationStrength;
            // ESTABILIDAD: el giro de este paso nunca debe superar el error restante. Con la fuerza por
            // defecto (100) a 72-90 Hz de física, fuerza × dt es MAYOR que 1: la muñeca giraría un
            // ~110-140% de su error en cada paso, sobrepasando el objetivo una y otra vez → oscilación
            // sostenida (tembleque), tanto mayor cuanto más rápido se mueve el mando. Con este tope
            // (|ángulo|/dt), como mucho llega exacta al objetivo en un paso, sin sobrepasarlo nunca.
            float maxNoOvershoot = Mathf.Abs(Mathf.Deg2Rad * angle) / deltaTime;
            multiLinear = Mathf.Clamp(multiLinear, -maxNoOvershoot, maxNoOvershoot);
            Vector3 angular = multiLinear * axis.normalized;
            angle = Mathf.Abs(angle);

            // El drag angular sube al acercarse en ángulo, para frenar sin oscilar.
            float inverseDeltaOffset = 0.011111f / Time.fixedDeltaTime;
            hand.body.angularDamping = Mathf.Lerp(startAngularDrag * angleDragDamper, startAngularDrag, angle / angleDragDamperDistance) * inverseDeltaOffset;

            hand.body.angularVelocity = angular;
            lastAngularVelocity = angular;
        }

        /// <summary>Ajusta la masa de la mano (y del objeto sostenido) según la distancia/ángulo al objetivo.</summary>
        protected virtual void SetMass() {
            // Punto/rotación actuales de referencia (el de agarre si sostenemos algo y no estamos agarrando).
            var currentPos = hand.holdingObj != null && !hand.IsGrabbing() ? hand.handGrabPoint.position : hand.transform.position;
            var currentRot = hand.holdingObj != null && !hand.IsGrabbing() ? hand.handGrabPoint.rotation : hand.transform.rotation;

            // Cuánto de lejos estamos en posición y en ángulo (0 = encima, 1 = en el máximo).
            float lerpPoint = Vector3.Distance(moveTo.position, currentPos) / distanceMassMaxDistance;
            float angleLerpPoint = Mathf.Abs(Quaternion.Angle(moveTo.rotation, currentRot)) / angleMassMaxAngle;
            float totalDiff = distanceMassDifference + angleMassDifference;

            // Masa dinámica = mezcla de una componente por distancia y otra por ángulo, ponderadas.
            float distanceMass = Mathf.Lerp(minMass, maxMass, lerpPoint) * distanceMassDifference / totalDiff;
            float angleMass = Mathf.Lerp(minMass, maxMass, angleLerpPoint) * angleMassDifference / totalDiff;
            targetMass = angleMass + distanceMass;
            hand.body.mass = targetMass;

            // Si sostenemos un objeto, también ajustamos su masa de forma proporcional (lo hace estable y manejable).
            if (hand.holdingObj != null && !hand.IsGrabbing() && hand.holdingObj.body != null) {
                float startHeldMass = hand.holdingObj.targetMass / heldMassDivider;
                float heldDistanceMass = Mathf.Lerp(startHeldMass * (minMass / maxMass), startHeldMass, lerpPoint) * distanceMassDifference / totalDiff;
                float heldAngleMass = Mathf.Lerp(startHeldMass * (minMass / maxMass), startHeldMass, angleLerpPoint) * angleMassDifference / totalDiff;
                targetHeldMass = heldAngleMass + heldDistanceMass;
                hand.holdingObj.body.mass = targetHeldMass;
            }
        }

        /// <summary>Recalcula el objetivo intermedio (moveTo) a partir del follow y los offsets de agarre.</summary>
        public virtual void SetMoveTo() {
            // Necesitamos un follow válido.
            if (follow == null && hand.follow == null)
                return;
            if (follow == null)
                follow = hand.follow;

            // Objetivo = pose del mando + el offset fijo de seguimiento + los offsets de agarre (que decaen tras agarrar).
            // El offset de posición se transforma por la rotación del mando, así queda en su espacio local (se mueve/gira con él).
            // El offset de rotación se compone con la rotación del mando (también en su espacio local).
            var targetPos = follow.position + follow.rotation * hand.followPositionOffset + hand.grabPositionOffset;
            var targetRot = follow.rotation * Quaternion.Euler(hand.followRotationOffset) * hand.grabRotationOffset;

            // Si sostenemos algo, añadimos el offset de sujeción del objeto (reflejado para la mano izquierda).
            if (hand.holdingObj != null) {
                if (hand.left) {
                    var moveLeft = hand.holdingObj.heldPositionOffset; moveLeft.x *= -1;
                    var leftRot = -hand.holdingObj.heldRotationOffset; leftRot.x *= -1;
                    targetPos += transform.rotation * moveLeft;
                    targetRot *= Quaternion.Euler(leftRot);
                }
                else {
                    targetPos += transform.rotation * hand.holdingObj.heldPositionOffset;
                    targetRot *= Quaternion.Euler(hand.holdingObj.heldRotationOffset);
                }
            }

            // Movemos el moveTo hacia el objetivo con un avance que crece con la raíz de la distancia:
            // así la "fuerza" es más fuerte cuanto más lejos, pero sin saltos bruscos que desestabilicen.
            if (hand.holdingObj != null && !hand.IsGrabbing()) {
                var distance = Vector3.Distance(targetPos, hand.handGrabPoint.position);
                var angleDistance = Quaternion.Angle(targetRot, hand.handGrabPoint.rotation);
                moveTo.position = Vector3.MoveTowards(hand.handGrabPoint.position, targetPos, maxMoveToDistance + Mathf.Sqrt(distance + 1f) - 1f);
                moveTo.rotation = Quaternion.RotateTowards(hand.handGrabPoint.rotation, targetRot, maxMoveToAngle + Mathf.Sqrt(angleDistance + 1f) - 1f);
            }
            else {
                var distance = Vector3.Distance(targetPos, hand.transform.position);
                var angleDistance = Quaternion.Angle(targetRot, hand.transform.rotation);
                moveTo.position = Vector3.MoveTowards(hand.transform.position, targetPos, maxMoveToDistance + Mathf.Sqrt(distance + 1f) - 1f);
                moveTo.rotation = Quaternion.RotateTowards(hand.transform.rotation, targetRot, maxMoveToAngle + Mathf.Sqrt(angleDistance + 1f) - 1f);
            }

            // Guardamos el objetivo "real" (sin el suavizado del moveTo) para el chequeo de distancia máxima.
            targetMoveToPosition = targetPos;
            targetMoveToRotation = targetRot;

            CheckHandMaxDistance();
        }

        /// <summary>Vigila la distancia mano↔objetivo: devuelve la mano si se aleja demasiado y, si sostiene un objeto que algo retiene (atascado o atado, p. ej. a una cuerda), lo suelta tras unos pasos en vez de arrastrarlo.</summary>
        protected virtual void CheckHandMaxDistance() {
            // Durante la animación de agarre la mano se aleja del mando a propósito: no vigilamos.
            if (hand.IsGrabbing())
                return;

            // Posición de referencia: el punto de agarre si sostiene algo, o la propia mano.
            var currentHandPos = hand.holdingObj != null ? hand.handGrabPoint.position : hand.transform.position;
            var distance = Vector3.Distance(currentHandPos, targetMoveToPosition);

            // ¿La mano se ha alejado más de lo permitido?
            if (distance > maxFollowDistance) {
                if (hand.holdingObj != null) {
                    // Sosteniendo un objeto NO se teletransporta: si una restricción externa lo retiene
                    // (una cuerda con tope físico, otro joint, geometría), recolocar mano+objeto a la
                    // fuerza viola esa restricción, la física los devuelve de un tirón y se entra en un
                    // bucle de saltos erráticos (teletransporte → tirón de vuelta → teletransporte...).
                    // En su lugar contamos pasos seguidos fuera de rango, por si es un atasco momentáneo...
                    tryMaxDistanceCount++;
                    if (tryMaxDistanceCount > maxDistanceReleaseFrames) {
                        // ...y si persiste, la mano "pierde el agarre" (algo tira más fuerte que ella,
                        // físicamente razonable) y vuelve sola al mando, sin arrastrar el objeto.
                        hand.holdingObj.ForceHandRelease(hand);
                        SetHandLocation(targetMoveToPosition, hand.transform.rotation);
                        tryMaxDistanceCount = 0;
                    }
                }
                else {
                    // Sin objeto, simplemente devolvemos la mano (seguridad anti-atasco).
                    SetHandLocation(targetMoveToPosition, hand.transform.rotation);
                }
            }
            // Dentro del rango: la "paciencia" se recupera poco a poco.
            else if (tryMaxDistanceCount > 0) {
                tryMaxDistanceCount--;
            }
        }

        /// <summary>Teletransporta la mano (y el objeto parentado que sostiene, conservando su transform relativo) a la pose dada.</summary>
        public virtual void SetHandLocation(Vector3 targetPosition, Quaternion targetRotation) {
            // Marcamos para anular la velocidad el próximo FixedUpdate (evita que salga disparada tras el salto).
            ignoreMoveFrame = true;

            // Un teletransporte NO debe interpolarse: si la interpolación quedara activa, la mano se
            // dibujaría "viajando" desde el sitio antiguo al nuevo durante un frame (estela visual).
            // La apagamos un instante (esto resetea su búfer interno) y la restauramos tras el salto.
            var prevHandInterpolation = hand.body.interpolation;
            hand.body.interpolation = RigidbodyInterpolation.None;

            // Si sostenemos un objeto parentado, lo movemos con la mano manteniendo su posición relativa.
            if (hand.holdingObj != null && hand.holdingObj.parentOnGrab && !hand.IsGrabbing() && hand.holdingObj.body != null) {
                var grab = hand.holdingObj.body;
                // Mismo tratamiento de interpolación para el objeto: salta, no se "estira".
                var prevGrabInterpolation = grab.interpolation;
                grab.interpolation = RigidbodyInterpolation.None;

                var handPos = hand.transform.position;
                var handRot = hand.transform.rotation;
                // Guardamos la posición/rotación del objeto relativas a la mano...
                var relPos = Quaternion.Inverse(handRot) * (grab.position - handPos);
                var relRot = Quaternion.Inverse(handRot) * grab.rotation;

                // ...y las reaplicamos respecto a la nueva pose de la mano.
                grab.position = targetPosition + targetRotation * relPos;
                grab.rotation = targetRotation * relRot;
                grab.transform.position = grab.position;
                grab.transform.rotation = grab.rotation;
                grab.linearVelocity = Vector3.zero;
                grab.angularVelocity = Vector3.zero;

                grab.interpolation = prevGrabInterpolation;
            }

            // Movemos la mano (transform y Rigidbody) y anulamos su velocidad.
            hand.transform.position = targetPosition;
            hand.transform.rotation = targetRotation;
            hand.body.position = targetPosition;
            hand.body.rotation = targetRotation;
            hand.body.linearVelocity = Vector3.zero;
            hand.body.angularVelocity = Vector3.zero;

            // Restauramos la interpolación de la mano (el salto ya está consumado).
            hand.body.interpolation = prevHandInterpolation;

            // El objetivo intermedio salta también, para no tirar de la mano de vuelta al sitio anterior.
            moveTo.position = targetPosition;
            moveTo.rotation = targetRotation;
        }

        /// <summary>Teletransporta la mano manteniendo su rotación actual.</summary>
        public virtual void SetHandLocation(Vector3 targetPosition) {
            SetHandLocation(targetPosition, hand.transform.rotation);
        }

        /// <summary>Devuelve la mano al objetivo intermedio actual.</summary>
        public void ResetHandLocation() {
            SetHandLocation(moveTo.position, moveTo.rotation);
        }
    }
}
