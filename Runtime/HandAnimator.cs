using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Posa los dedos de forma procedural cada frame. Mezcla una pose "de input" en vivo
    /// (abierto↔cerrado según el grip de la mano más un balanceo por velocidad) con una pose
    /// "objetivo" (una pose de agarre), usando una curva de animación temporizada. Mientras hay un
    /// agarre en curso, <see cref="Hand"/> controla los dedos directamente, así que este componente
    /// cede el control durante esa ventana.
    /// </summary>
    [RequireComponent(typeof(Hand))]
    [DefaultExecutionOrder(10000)]
    public class HandAnimator : MonoBehaviour {

        Hand hand;

        [Label("Tiempo de transición de pose", "Tiempo por defecto para mezclar al entrar/salir de una pose objetivo.")]
        public float defaultPoseTransitionTime = 0.3f;
        [Label("Curva de transición de pose", "Curva por defecto usada en las transiciones de pose.")]
        public AnimationCurve defaultPoseTransitionCurve = AnimationCurve.Linear(0, 0, 1, 1);

        // --- Poses reutilizables (ref properties): se crean perezosamente y se reutilizan sin reservar memoria ---

        HandPoseData _handPoseDataNonAlloc;
        /// <summary>Pose "borrador" reutilizada por la animación de agarre para evitar reservas de memoria.</summary>
        internal ref HandPoseData handPoseDataNonAlloc {
            get { if (!_handPoseDataNonAlloc.isSet) _handPoseDataNonAlloc = new HandPoseData(hand); return ref _handPoseDataNonAlloc; }
        }

        HandPoseData _openHandPose;
        /// <summary>Pose de mano totalmente abierta, montada a partir de la pose Open de cada dedo.</summary>
        public ref HandPoseData openHandPose {
            get { if (!_openHandPose.isSet) _openHandPose = new HandPoseData(hand); return ref _openHandPose; }
        }

        HandPoseData _closeHandPose;
        /// <summary>Pose de mano totalmente cerrada, montada a partir de la pose Closed de cada dedo.</summary>
        public ref HandPoseData closeHandPose {
            get { if (!_closeHandPose.isSet) _closeHandPose = new HandPoseData(hand); return ref _closeHandPose; }
        }

        HandPoseData _targetGrabPose;
        /// <summary>La pose en la que la mano debe sostener el objeto actual.</summary>
        public ref HandPoseData targetGrabPose {
            get { if (!_targetGrabPose.isSet) _targetGrabPose = new HandPoseData(hand); return ref _targetGrabPose; }
        }

        HandPoseData _currentInputPose;
        // Pose en vivo derivada del input (grip + sway).
        ref HandPoseData currentInputPose {
            get { if (!_currentInputPose.isSet) _currentInputPose = new HandPoseData(hand); return ref _currentInputPose; }
        }

        HandPoseData _currentTargetPose;
        // Pose objetivo activa (p. ej. la de agarre) hacia la que estamos transicionando.
        ref HandPoseData currentTargetPose {
            get { if (!_currentTargetPose.isSet) _currentTargetPose = new HandPoseData(hand); return ref _currentTargetPose; }
        }

        HandPoseData _currentHandPose;
        // Resultado de mezclar input↔target según el estado de transición.
        ref HandPoseData currentHandPose {
            get { if (!_currentHandPose.isSet) _currentHandPose = new HandPoseData(hand); return ref _currentHandPose; }
        }

        HandPoseData _currentHandSmoothPose;
        // Versión suavizada (la que finalmente se aplica a los dedos) para que no haya saltos bruscos.
        ref HandPoseData currentHandSmoothPose {
            get { if (!_currentHandSmoothPose.isSet) _currentHandSmoothPose = new HandPoseData(hand); return ref _currentHandSmoothPose; }
        }

        float targetPoseStartTransitionTime; // momento en que empezó la transición hacia la pose objetivo
        float targetPoseStopTransitionTime;  // momento en que empezó la transición de vuelta al input
        float targetPoseTotalTransitionTime; // duración de la transición actual
        AnimationCurve targetTransitionAnimationCurve;
        bool poseActive;     // true si hay una pose objetivo activa
        float fingerSwayVel; // velocidad suavizada usada para el balanceo (sway) de los dedos

        protected virtual void Awake() {
            hand = GetComponent<Hand>();
        }

        protected virtual void Start() {
            // Montamos las poses abierta y cerrada de la mano a partir de las poses Open/Closed de cada dedo.
            for (int i = 0; i < hand.fingers.Length; i++) {
                var finger = hand.fingers[i];
                if (finger == null || finger.fingerType == FingerEnum.none)
                    continue;
                int idx = (int)finger.fingerType;
                if (finger.IsPoseSaved(FingerPoseEnum.Open))
                    openHandPose.fingerPoses[idx].CopyFromData(ref finger.poseData[(int)FingerPoseEnum.Open]);
                if (finger.IsPoseSaved(FingerPoseEnum.Closed))
                    closeHandPose.fingerPoses[idx].CopyFromData(ref finger.poseData[(int)FingerPoseEnum.Closed]);
            }

            // Garantizamos que la curva por defecto es válida.
            if (defaultPoseTransitionCurve == null || defaultPoseTransitionCurve.keys.Length == 0)
                defaultPoseTransitionCurve = AnimationCurve.Linear(0, 0, 1, 1);
            targetTransitionAnimationCurve = defaultPoseTransitionCurve;
        }

        protected virtual void LateUpdate() {
            // En LateUpdate (tras animaciones/físicas) recalculamos y aplicamos las poses. Si el IK está
            // desactivado, no tocamos los dedos (los controla otra cosa).
            if (!hand.enableIK)
                return;
            UpdateInputPoseState();
            UpdateTargetPoseState();
        }

        /// <summary>Recalcula la pose de input en vivo (grip + balanceo por velocidad) en <see cref="currentInputPose"/>.</summary>
        protected virtual void UpdateInputPoseState() {
            // Calculamos una velocidad media a partir del historial de posiciones de la mano (para el sway).
            var tracked = hand.handFollow.updatePositionTracked;
            var averageVel = Vector3.zero;
            for (int i = 1; i < tracked.Length; i++)
                averageVel += tracked[i] - tracked[i - 1];
            averageVel /= tracked.Length;

            // Pasamos la velocidad al espacio de la palma para usar su componente "hacia delante" (Z).
            if (transform.parent != null)
                averageVel = (Quaternion.Inverse(hand.palmTransform.rotation) * transform.parent.rotation) * averageVel;

            float vel = (averageVel * 60f).z;
            // Si la mano está chocando con algo, anulamos el sway (no tendría sentido balancear contra una pared).
            if (hand.CollisionCount() > 0)
                vel = 0;
            // Suavizamos la velocidad de sway hacia el valor objetivo (cuanto mayor el cambio, más rápido reacciona).
            fingerSwayVel = Mathf.MoveTowards(fingerSwayVel, vel, Time.deltaTime * Mathf.Abs((fingerSwayVel - vel) * 30f));

            // Flexión de reposo (gripOffset) mezclada hacia el cierre total según el grip/squeeze del mando.
            float gripInput = Mathf.Clamp01(Mathf.Max(hand.GetGripAxis(), hand.GetSqueezeAxis()));
            float grip = Mathf.Lerp(hand.gripOffset, 1f, gripInput) + hand.swayStrength * fingerSwayVel;
            // Aplicamos a cada dedo: interpolamos su pose abierta→cerrada por (grip + su flexión propia).
            foreach (var finger in hand.fingers) {
                if (finger == null || finger.fingerType == FingerEnum.none)
                    continue;
                int idx = (int)finger.fingerType;
                // Saltamos dedos cuya pose abierta/cerrada no esté lista (evita NRE si faltan poses).
                if (!openHandPose.fingerPoses[idx].isLocalSet || !closeHandPose.fingerPoses[idx].isLocalSet)
                    continue;
                currentInputPose.fingerPoses[idx].LerpData(ref openHandPose.fingerPoses[idx], ref closeHandPose.fingerPoses[idx], grip + finger.GetCurrentBend());
            }
        }

        /// <summary>Mezcla la pose de input hacia la pose objetivo activa y la aplica (salvo si se está agarrando).</summary>
        protected virtual void UpdateTargetPoseState() {
            // 'state' va de 0 (pose de input) a 1 (pose objetivo), según el tiempo transcurrido y la curva.
            float state;
            if (targetPoseTotalTransitionTime <= 0f)
                // Sin tiempo de transición: salto directo (1 si hay pose activa, 0 si no).
                state = poseActive ? 1f : 0f;
            else if (poseActive)
                // Transición hacia la pose objetivo.
                state = targetTransitionAnimationCurve.Evaluate(Mathf.Clamp01((Time.time - targetPoseStartTransitionTime) / targetPoseTotalTransitionTime));
            else
                // Transición de vuelta a la pose de input (de 1 a 0).
                state = targetTransitionAnimationCurve.Evaluate(1f - Mathf.Clamp01((Time.time - targetPoseStopTransitionTime) / targetPoseTotalTransitionTime));

            // Componemos la pose actual: mezcla si estamos en transición, o copia directa en los extremos.
            if (state < 1f)
                currentHandPose.LerpPose(ref currentInputPose, ref currentTargetPose, state);
            else if (poseActive)
                currentHandPose.CopyFromData(ref currentTargetPose);
            else
                currentHandPose.CopyFromData(ref currentInputPose);

            // Suavizamos hacia la pose calculada (33% por frame) para eliminar saltos, y la aplicamos a los dedos.
            currentHandSmoothPose.LerpPose(ref currentHandSmoothPose, ref currentHandPose, 0.33f);
            // Mientras se agarra, manda la coroutine de Hand: no pisamos los dedos.
            if (!hand.IsGrabbing())
                currentHandSmoothPose.SetFingerPose(hand);
        }

        /// <summary>Empieza a mezclar la mano hacia <paramref name="poseData"/> durante <paramref name="transitionTime"/> segundos.</summary>
        public virtual void SetTargetPose(ref HandPoseData poseData, float transitionTime, AnimationCurve animationCurve) {
            targetTransitionAnimationCurve = animationCurve != null ? animationCurve : defaultPoseTransitionCurve;
            targetPoseTotalTransitionTime = transitionTime;
            targetPoseStartTransitionTime = Time.time;
            poseActive = true;
            currentTargetPose.CopyFromData(ref poseData);
            // Si la transición es instantánea, fijamos ya la pose actual.
            if (transitionTime == 0)
                currentHandPose.CopyFromData(ref poseData);
        }

        /// <summary>Fija una pose objetivo usando la curva por defecto.</summary>
        public void SetPose(ref HandPoseData pose, float transitionTime) => SetTargetPose(ref pose, transitionTime, defaultPoseTransitionCurve);
        /// <summary>Fija una pose objetivo usando el tiempo y la curva por defecto.</summary>
        public void SetPose(ref HandPoseData pose) => SetTargetPose(ref pose, defaultPoseTransitionTime, defaultPoseTransitionCurve);

        /// <summary>Detiene la pose objetivo activa, volviendo a la pose de input en <paramref name="cancelTime"/> segundos.</summary>
        public void CancelPose(float cancelTime) {
            targetPoseTotalTransitionTime = cancelTime;
            targetPoseStopTransitionTime = Time.time;
            poseActive = false;
        }

        /// <summary>Detiene la pose objetivo activa usando el tiempo de transición por defecto.</summary>
        public void CancelPose() => CancelPose(defaultPoseTransitionTime);

        /// <summary>Indica si hay una pose objetivo activa o en transición ahora mismo.</summary>
        public bool IsPosing() {
            return poseActive
                || (hand.holdingObj != null && hand.holdingObj.HasCustomPose())
                || (Time.time - targetPoseStartTransitionTime < targetPoseTotalTransitionTime);
        }
    }
}
