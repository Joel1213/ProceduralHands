using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ProceduralHands {
    /// <summary>
    /// Pose de agarre restringida a una rotación alrededor de un eje y/o un deslizamiento a lo largo
    /// de él — para objetos que se agarran a orientaciones variables, como el mango de un bate, un
    /// volante o una palanca. A partir de una única pose base guardada, elige el ángulo (dentro de
    /// [<see cref="minAngle"/>, <see cref="maxAngle"/>]) y el desplazamiento (dentro de
    /// [<see cref="minRange"/>, <see cref="maxRange"/>]) que mejor encajan con dónde está alcanzando la
    /// mano, y la posa ahí.
    /// </summary>
    public class GrabbablePoseAdvanced : GrabbablePose {

        [Label("Objeto centro (pivote)", "Pivote alternativo opcional, cuando el transform del objeto no está centrado en el eje deseado.")]
        public Transform centerObject;
        [Space]
        [Label("Eje (up)", "Eje local alrededor del cual la mano puede rotar / a lo largo del cual deslizar (el disco/línea del gizmo).")]
        public Vector3 up = Vector3.up;
        [Space]
        [Label("Permitir pose invertida", "Permite además la pose girada 180° (p. ej. agarrar un martillo del derecho o del revés).")]
        public bool useInvertPose = false;

        [Space]
        [Label("Ángulo mínimo", "Rotación mínima (grados) alrededor del eje.")]
        public int minAngle = 0;
        [Label("Ángulo máximo", "Rotación máxima (grados) alrededor del eje.")]
        public int maxAngle = 360;
        [Space]
        [Label("Rango mínimo", "Distancia mínima de deslizamiento a lo largo del eje.")]
        public float minRange = 0;
        [Label("Rango máximo", "Distancia máxima de deslizamiento a lo largo del eje.")]
        public float maxRange = 0;

        [Header("Prueba en editor (requiere Gizmos)")]
        [Label("Ángulo de prueba", "Ángulo de previsualización aplicado a la mano del editor.")]
        public int testAngle = 0;
        [Label("Rango de prueba", "Rango de previsualización aplicado a la mano del editor.")]
        public float testRange = 0;

        HandPoseData _advancedNonAlloc; // pose reutilizable que devolvemos por referencia

        protected override void Awake() {
            base.Awake();
            // Aseguramos que min <= max (si están al revés, los intercambiamos).
            if (minAngle > maxAngle) (minAngle, maxAngle) = (maxAngle, minAngle);
            if (minRange > maxRange) (minRange, maxRange) = (maxRange, minRange);
        }

        // El pivote del eje: el objeto centro si se asignó, o este transform.
        Transform GetTransform() => centerObject != null ? centerObject : transform;

        // Un eje perpendicular al 'up', usado para la pose invertida (girar 180° en otro eje).
        Vector3 AdditionalDirection() {
            if (Mathf.Abs(up.x) > 0.0001f) return Vector3.up;
            if (Mathf.Abs(up.y) > 0.0001f) return Vector3.right;
            return Vector3.forward;
        }

        /// <summary>Calcula la pose restringida más cercana a la aproximación actual de la mano y la devuelve.</summary>
        public override ref HandPoseData GetHandPoseData(Hand hand) {
            // Guardamos dónde está la mano AHORA (su aproximación) para acercar la pose a este punto y restaurar luego.
            Vector3 pregrabPos = hand.transform.position;
            Quaternion pregrabRot = hand.transform.rotation;

            // Capturamos la mano pre-agarre para restaurarla al final, y movemos la mano a la pose base.
            var preGrabPose = new HandPoseData(hand, transform);
            base.GetHandPoseData(hand.left).SetPose(hand, transform);

            Transform getT = GetTransform();
            Vector3 pivot = getT.position;
            // Eje en mundo alrededor del cual rotamos/deslizamos.
            Vector3 axis = (getT.rotation * up).normalized;
            if (axis.sqrMagnitude < 1e-6f)
                axis = Vector3.up;

            // Pose base de la mano (tras aplicar la pose guardada): es el punto de partida que rotaremos/deslizaremos.
            Vector3 baseHandPos = hand.transform.position;
            Quaternion baseHandRot = hand.transform.rotation;

            // 1) Mejor rotación alrededor del eje (con posible giro de 180°), para acercar la mano al pregrab.
            float bestAngle = FindBestAngle(getT, pivot, axis, baseHandPos, baseHandRot, pregrabPos, pregrabRot, out Quaternion extraRot);
            Quaternion rot = Quaternion.AngleAxis(bestAngle, axis) * extraRot;
            // Aplicamos esa rotación a la pose base, girando alrededor del pivote.
            Vector3 rotatedPos = pivot + rot * (baseHandPos - pivot);
            Quaternion rotatedRot = rot * baseHandRot;

            // 2) Mejor deslizamiento a lo largo del eje, para acercar aún más al pregrab.
            float bestRange = FindBestRange(axis, rotatedPos, pregrabPos);
            Vector3 finalPos = rotatedPos + axis * bestRange;

            // Colocamos la mano en la pose final y la guardamos en la pose reutilizable.
            hand.transform.SetPositionAndRotation(finalPos, rotatedRot);
            if (!_advancedNonAlloc.isSet)
                _advancedNonAlloc = new HandPoseData(hand, transform);
            else
                _advancedNonAlloc.SavePose(hand, transform);

            // Restauramos la mano a donde estaba antes de esta consulta (no queremos moverla de verdad aquí).
            preGrabPose.SetPose(hand);

            return ref _advancedNonAlloc;
        }

        // Busca el ángulo (en [minAngle,maxAngle]) que deja la mano lo más cerca posible del pregrab.
        float FindBestAngle(Transform getT, Vector3 pivot, Vector3 axis, Vector3 h0Pos, Quaternion h0Rot, Vector3 targetPos, Quaternion targetRot, out Quaternion extraRot) {
            extraRot = Quaternion.identity;

            // Paso de la búsqueda gruesa: el rango dividido en ~10 muestras.
            float span = Mathf.Abs(maxAngle - minAngle);
            float iteration = span / 10f;
            if (iteration <= 0.0001f) {
                // Sin libertad de rotación: solo evaluamos el mínimo (y la inversa si procede).
                if (useInvertPose) {
                    Quaternion flip = Quaternion.AngleAxis(180f, getT.rotation * AdditionalDirection());
                    if (Eval(minAngle, flip) < Eval(minAngle, Quaternion.identity))
                        extraRot = flip;
                }
                return minAngle;
            }

            // Pasada gruesa: probamos ángulos a saltos grandes y nos quedamos con el mejor.
            float best = minAngle;
            float bestDist = float.MaxValue;
            for (float a = minAngle; a <= maxAngle; a += iteration) {
                float d = Eval(a, Quaternion.identity);
                if (d < bestDist) { bestDist = d; best = a; }
            }
            // Pasada fina: afinamos alrededor del mejor ángulo grueso.
            for (float a = best - iteration; a <= best + iteration; a += iteration / 10f) {
                if (a < minAngle || a > maxAngle) continue;
                float d = Eval(a, Quaternion.identity);
                if (d < bestDist) { bestDist = d; best = a; }
            }

            // Si se permite la pose invertida, repetimos la búsqueda con un giro extra de 180° y comparamos.
            if (useInvertPose) {
                Quaternion flip = Quaternion.AngleAxis(180f, getT.rotation * AdditionalDirection());
                float bestInv = minAngle;
                bool foundInv = false;
                for (float a = minAngle; a <= maxAngle; a += iteration) {
                    float d = Eval(a, flip);
                    if (d < bestDist) { bestDist = d; bestInv = a; foundInv = true; }
                }
                for (float a = bestInv - iteration; a <= bestInv + iteration; a += iteration / 10f) {
                    if (a < minAngle || a > maxAngle) continue;
                    float d = Eval(a, flip);
                    if (d < bestDist) { bestDist = d; bestInv = a; foundInv = true; }
                }
                // Si la variante invertida ganó, devolvemos su ángulo y marcamos el giro extra.
                if (foundInv) { extraRot = flip; return bestInv; }
            }

            return best;

            // "Coste" de un ángulo: cuánto se aleja la mano resultante del pregrab (posición + ángulo).
            float Eval(float a, Quaternion extra) {
                Quaternion r = Quaternion.AngleAxis(a, axis) * extra;
                Vector3 p = pivot + r * (h0Pos - pivot);
                Quaternion rr = r * h0Rot;
                return Vector3.Distance(p, targetPos) + Quaternion.Angle(rr, targetRot) / 180f;
            }
        }

        // Busca el desplazamiento a lo largo del eje (en [minRange,maxRange]) que más acerca al pregrab.
        float FindBestRange(Vector3 axis, Vector3 rotatedPos, Vector3 targetPos) {
            // Sin rango configurado, no deslizamos.
            if (Mathf.Approximately(minRange, 0f) && Mathf.Approximately(maxRange, 0f))
                return 0f;

            // Pasada gruesa: 11 muestras entre min y max.
            float best = minRange;
            float bestDist = float.MaxValue;
            for (int i = 0; i <= 10; i++) {
                float d = Mathf.Lerp(minRange, maxRange, i / 10f);
                float dist = Vector3.Distance(rotatedPos + axis * d, targetPos);
                if (dist < bestDist) { bestDist = dist; best = d; }
            }

            // Pasada fina: afinamos alrededor del mejor desplazamiento grueso.
            float fineSpan = (maxRange - minRange) / 20f;
            float fineStep = (maxRange - minRange) / 100f;
            if (Mathf.Abs(fineStep) > 1e-6f) {
                for (float d = best - fineSpan; d <= best + fineSpan; d += fineStep) {
                    if (d < minRange || d > maxRange) continue;
                    float dist = Vector3.Distance(rotatedPos + axis * d, targetPos);
                    if (dist < bestDist) { bestDist = dist; best = d; }
                }
            }
            return best;
        }

#if UNITY_EDITOR
        /// <summary>Editor: posa la mano en un ángulo/rango de prueba concretos para previsualizar en la escena.</summary>
        public void EditorTestValues(Hand editorHand) {
            if (editorHand == null)
                return;
            // Normalizamos min/max y limitamos los valores de prueba a su rango.
            if (minAngle > maxAngle) (minAngle, maxAngle) = (maxAngle, minAngle);
            if (minRange > maxRange) (minRange, maxRange) = (maxRange, minRange);
            testAngle = Mathf.Clamp(testAngle, minAngle, maxAngle);
            testRange = Mathf.Clamp(testRange, minRange, maxRange);

            // Partimos de la pose base y aplicamos la rotación y el deslizamiento de prueba.
            base.GetHandPoseData(editorHand.left).SetPose(editorHand, transform);

            Transform getT = GetTransform();
            Vector3 pivot = getT.position;
            Vector3 axis = (getT.rotation * up).normalized;
            Quaternion rot = Quaternion.AngleAxis(testAngle, axis);

            Vector3 pos = pivot + rot * (editorHand.transform.position - pivot) + axis * testRange;
            Quaternion rotation = rot * editorHand.transform.rotation;
            editorHand.transform.SetPositionAndRotation(pos, rotation);
        }

        protected override void OnDrawGizmosSelected() {
            if (Application.isPlaying)
                return;
            base.OnDrawGizmosSelected();

            Transform getT = GetTransform();
            Vector3 axis = getT.rotation * up;
            float radius = 0.1f;

            // Arco que representa el rango de ángulos permitido alrededor del eje.
            Handles.color = Color.white;
            Vector3 from = Quaternion.AngleAxis(minAngle, axis) * (Mathf.Abs(Vector3.Dot(axis.normalized, Vector3.forward)) > 0.9f ? getT.right : Vector3.Cross(axis, Vector3.up).normalized);
            Handles.DrawWireArc(getT.position, axis, from, maxAngle - minAngle, radius);

            // Líneas que representan el rango de deslizamiento (min y max) a lo largo del eje.
            Gizmos.color = Color.white;
            Gizmos.DrawLine(getT.position, getT.position + axis.normalized * minRange);
            Gizmos.DrawLine(getT.position, getT.position + axis.normalized * maxRange);
        }
#endif
    }
}
