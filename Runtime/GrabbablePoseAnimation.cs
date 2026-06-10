using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Anima la mano sostenida entre dos poses guardadas (<see cref="fromPose"/> → <see cref="toPose"/>)
    /// según el eje del gatillo o del grip del mando, y opcionalmente anima transforms hijos con el
    /// mismo valor (p. ej. el gatillo de una pistola o una palanca). Se ejecuta después de
    /// <see cref="HandAnimator"/> para sobrescribir la pose de los dedos mientras se sostiene el objeto.
    /// </summary>
    [DefaultExecutionOrder(10001)]
    [RequireComponent(typeof(Grabbable))]
    public class GrabbablePoseAnimation : MonoBehaviour {

        public enum AnimationInputAxis { Trigger, Grip }

        [Label("Eje de input", "Qué eje del mando dirige la animación: gatillo (Trigger) o grip.")]
        public AnimationInputAxis inputAxis = AnimationInputAxis.Trigger;
        [Label("Curva", "Reasigna el input bruto 0..1 al punto de animación (respuesta del gatillo).")]
        public AnimationCurve curve = AnimationCurve.Linear(0, 0, 1, 1);

        [Label("Pose inicial (from)", "Pose con el input a 0 (p. ej. gatillo sin apretar).")]
        public GrabbablePose fromPose;
        [Label("Pose final (to)", "Pose con el input a 1 (p. ej. gatillo apretado).")]
        public GrabbablePose toPose;

        [Header("Animación de transforms hijos (opcional)")]
        [Label("Transforms animados", "Transforms que se animan entre su pose local start/end guardada con el mismo input.")]
        public Transform[] animatedTransforms = new Transform[0];

        // Poses local (pos/rot) start y end de los transforms hijos; se graban con los menús contextuales.
        [SerializeField, HideInInspector] Vector3[] startPositions = new Vector3[0];
        [SerializeField, HideInInspector] Quaternion[] startRotations = new Quaternion[0];
        [SerializeField, HideInInspector] Vector3[] endPositions = new Vector3[0];
        [SerializeField, HideInInspector] Quaternion[] endRotations = new Quaternion[0];

        Grabbable grabbable;
        HandPoseData lerpPose; // pose reutilizable donde calculamos la mezcla from→to

        void Awake() {
            grabbable = GetComponent<Grabbable>();
        }

        void LateUpdate() {
            // Solo animamos mientras el objeto está sostenido.
            if (grabbable == null || !grabbable.IsHeld())
                return;

            var hands = grabbable.GetHeldBy();
            float driverInput = 0f; // input que dirigirá la animación de los transforms hijos

            for (int h = 0; h < hands.Count; h++) {
                var hand = hands[h];
                // "Trigger" sigue el eje squeeze de la mano (bindeado por defecto al gatillo del mando);
                // "Grip" sigue el eje grip (bindeado por defecto al grip del mando).
                float raw = Mathf.Clamp01(inputAxis == AnimationInputAxis.Trigger ? hand.GetSqueezeAxis() : hand.GetGripAxis());
                // La animación de los hijos la dirige la primera mano.
                if (h == 0)
                    driverInput = raw;

                // Necesitamos ambas poses guardadas para el lado de esta mano.
                if (fromPose == null || toPose == null || !fromPose.HasPose(hand.left) || !toPose.HasPose(hand.left))
                    continue;

                // Interpolamos los dedos de from→to por la curva del input y los aplicamos a la mano.
                float t = curve.Evaluate(raw);
                var fromData = fromPose.GetHandPoseData(hand);
                var toData = toPose.GetHandPoseData(hand);
                if (!lerpPose.isSet)
                    lerpPose = new HandPoseData(hand);
                lerpPose.LerpPose(ref fromData, ref toData, t);
                lerpPose.SetFingerPose(hand);
            }

            // Animamos los transforms hijos (gatillo, etc.) con el mismo input.
            if (animatedTransforms.Length > 0)
                SetChildAnimation(curve.Evaluate(driverInput));
        }

        /// <summary>Coloca los transforms hijos animados en el punto de interpolación (0..1).</summary>
        public void SetChildAnimation(float point) {
            // Si no están grabados start y end (longitudes no coinciden), no animamos.
            if (startPositions.Length != animatedTransforms.Length || endPositions.Length != animatedTransforms.Length)
                return;
            for (int i = 0; i < animatedTransforms.Length; i++) {
                if (animatedTransforms[i] == null)
                    continue;
                // Interpolamos la pose LOCAL (relativa al padre) entre start y end.
                animatedTransforms[i].localPosition = Vector3.Lerp(startPositions[i], endPositions[i], point);
                animatedTransforms[i].localRotation = Quaternion.Lerp(startRotations[i], endRotations[i], point);
            }
        }

        // Menú contextual: graba la pose actual de los hijos como estado "start" (input 0).
        [ContextMenu("Guardar pose inicial de hijos")]
        void SaveChildStart() => RecordChildren(true);

        // Menú contextual: graba la pose actual de los hijos como estado "end" (input 1).
        [ContextMenu("Guardar pose final de hijos")]
        void SaveChildEnd() => RecordChildren(false);

        // Graba la pose local (pos/rot) de cada transform hijo en el array start o end.
        void RecordChildren(bool start) {
            int n = animatedTransforms.Length;
            // Ajustamos el tamaño de los arrays al número de transforms.
            if (startPositions.Length != n) { startPositions = new Vector3[n]; startRotations = new Quaternion[n]; }
            if (endPositions.Length != n) { endPositions = new Vector3[n]; endRotations = new Quaternion[n]; }

            for (int i = 0; i < n; i++) {
                if (animatedTransforms[i] == null)
                    continue;
                if (start) {
                    startPositions[i] = animatedTransforms[i].localPosition;
                    startRotations[i] = animatedTransforms[i].localRotation;
                }
                else {
                    endPositions[i] = animatedTransforms[i].localPosition;
                    endRotations[i] = animatedTransforms[i].localRotation;
                }
            }
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
    }
}
