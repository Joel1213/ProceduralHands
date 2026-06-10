using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Instantánea serializable de una pose completa de mano: el offset de posición/rotación de la mano
    /// respecto a un transform de referencia (un grabbable) más una <see cref="FingerPoseData"/> por
    /// dedo (indexada por <see cref="FingerEnum"/>). Es el formato común de guardar/cargar/mezclar que
    /// usan el animador de dedos, la animación de agarre y todos los componentes de pose de grabbable,
    /// por eso la lógica de interpolación vive aquí una sola vez.
    /// </summary>
    [System.Serializable]
    public struct HandPoseData {

        /// <summary>Posición de la mano en el espacio local del transform de referencia.</summary>
        public Vector3 handOffset;
        /// <summary>Rotación de la mano relativa al transform de referencia.</summary>
        public Quaternion localQuaternionOffset;
        /// <summary>Escala (lossyScale) de la mano capturada al guardar la pose.</summary>
        public Vector3 globalHandScale;
        /// <summary>Una pose de dedo por <see cref="FingerEnum"/> (longitud 5).</summary>
        public FingerPoseData[] fingerPoses;

        /// <summary>True cuando ya se han reservado las poses de dedos.</summary>
        public bool isSet => fingerPoses != null && fingerPoses.Length > 0;

        /// <summary>Crea una pose vacía a partir de la forma actual de <paramref name="hand"/> (sin referencia).</summary>
        public HandPoseData(Hand hand) {
            handOffset = Vector3.zero;
            localQuaternionOffset = Quaternion.identity;
            globalHandScale = hand.transform.lossyScale;
            fingerPoses = new FingerPoseData[5];
            SavePose(hand, null);
        }

        /// <summary>Crea una pose a partir de la forma actual de <paramref name="hand"/> relativa a <paramref name="relativeTo"/>.</summary>
        public HandPoseData(Hand hand, Transform relativeTo) {
            handOffset = Vector3.zero;
            localQuaternionOffset = Quaternion.identity;
            globalHandScale = hand.transform.lossyScale;
            fingerPoses = new FingerPoseData[5];
            SavePose(hand, relativeTo);
        }

        /// <summary>Crea una pose a partir de la forma actual de <paramref name="hand"/> relativa a un grabbable.</summary>
        public HandPoseData(Hand hand, Grabbable grabbable) : this(hand, grabbable.transform) { }

        /// <summary>Hace una copia profunda de otra pose.</summary>
        public HandPoseData(ref HandPoseData data) {
            handOffset = data.handOffset;
            localQuaternionOffset = data.localQuaternionOffset;
            globalHandScale = data.globalHandScale;
            fingerPoses = new FingerPoseData[5];
            // Copiamos profundamente cada dedo (no basta con copiar el array: comparte referencias).
            for (int i = 0; i < fingerPoses.Length && i < data.fingerPoses.Length; i++)
                fingerPoses[i] = new FingerPoseData(ref data.fingerPoses[i]);
        }

        /// <summary>Copia profundamente los offsets y las poses de dedo desde <paramref name="data"/> a esta pose.</summary>
        public void CopyFromData(ref HandPoseData data) {
            handOffset = data.handOffset;
            localQuaternionOffset = data.localQuaternionOffset;
            globalHandScale = data.globalHandScale;

            // Si aún no tenemos array de dedos, lo creamos y copiamos cada dedo como copia nueva...
            if (fingerPoses == null || fingerPoses.Length == 0) {
                fingerPoses = new FingerPoseData[5];
                for (int i = 0; i < fingerPoses.Length && i < data.fingerPoses.Length; i++)
                    fingerPoses[i] = new FingerPoseData(ref data.fingerPoses[i]);
            }
            // ...si ya lo tenemos, reutilizamos los arrays existentes copiando dentro (sin reservar memoria).
            else {
                for (int i = 0; i < fingerPoses.Length && i < data.fingerPoses.Length; i++)
                    fingerPoses[i].CopyFromData(ref data.fingerPoses[i]);
            }
        }

        /// <summary>
        /// Guarda la forma actual de <paramref name="hand"/> en esta pose. Si <paramref name="relativeTo"/>
        /// está definido, registra además el offset de posición/rotación de la mano respecto a ese
        /// transform; si no, el offset queda en identidad (pose de mano "en el aire").
        /// </summary>
        public void SavePose(Hand hand, Transform relativeTo = null) {
            if (fingerPoses == null || fingerPoses.Length < 5)
                fingerPoses = new FingerPoseData[5];

            // Guardamos la pose de cada dedo en su hueco según el tipo de dedo.
            foreach (var finger in hand.fingers) {
                // Saltamos dedos nulos o sin tipo asignado (su índice sería inválido).
                if (finger == null || finger.fingerType == FingerEnum.none)
                    continue;

                int fingerIndex = (int)finger.fingerType;
                // Si ese hueco aún no tenía datos creamos la pose; si ya los tenía la sobrescribimos sin reservar.
                if (!fingerPoses[fingerIndex].isSet)
                    fingerPoses[fingerIndex] = new FingerPoseData(hand, finger);
                else
                    fingerPoses[fingerIndex].SetPoseData(hand, finger);
            }

            // Registramos el offset de la mano respecto al transform de referencia (o identidad si no hay).
            if (relativeTo != null) {
                handOffset = relativeTo.InverseTransformPoint(hand.transform.position);          // posición en local del objeto
                localQuaternionOffset = Quaternion.Inverse(relativeTo.rotation) * hand.transform.rotation; // rotación relativa
                globalHandScale = hand.transform.lossyScale;
            }
            else {
                handOffset = Vector3.zero;
                localQuaternionOffset = Quaternion.identity;
                globalHandScale = hand.transform.lossyScale;
            }
        }

        /// <summary>Aplica a <paramref name="hand"/> tanto el offset de posición como la pose de los dedos.</summary>
        public void SetPose(Hand hand, Transform relativeTo = null) {
            SetPosition(hand, relativeTo);
            SetFingerPose(hand);
        }

        /// <summary>Aplica solo la pose de los dedos a <paramref name="hand"/>, sin tocar su posición.</summary>
        public void SetFingerPose(Hand hand) {
            foreach (var finger in hand.fingers) {
                if (finger == null || finger.fingerType == FingerEnum.none)
                    continue;
                fingerPoses[(int)finger.fingerType].SetFingerPose(finger);
            }
        }

        /// <summary>Mueve <paramref name="hand"/> (y su Rigidbody) al offset guardado respecto a <paramref name="relativeTo"/>.</summary>
        public void SetPosition(Hand hand, Transform relativeTo = null) {
            // Solo tiene sentido si hay una referencia distinta de la propia mano.
            if (relativeTo != null && relativeTo != hand.transform) {
                // Construimos la matriz de mundo que debe ocupar la mano y extraemos posición/rotación.
                Matrix4x4 handToWorld = GetHandToWorldMatrix(relativeTo);
                Vector3 newPosition = HandUtils.ExtractPosition(ref handToWorld);
                Quaternion newRotation = HandUtils.ExtractRotation(ref handToWorld);

                // Movemos el transform y, si hay Rigidbody, también su posición/rotación física (para que no haya desfase).
                hand.transform.SetPositionAndRotation(newPosition, newRotation);
                if (hand.body != null) {
                    hand.body.position = newPosition;
                    hand.body.rotation = newRotation;
                }
            }
        }

        /// <summary>Construye la matriz de mundo que debe ocupar la mano respecto a <paramref name="relativeTo"/>.</summary>
        public Matrix4x4 GetHandToWorldMatrix(Transform relativeTo) {
            // Sin referencia, el offset se interpreta directamente como una transformada de mundo.
            if (relativeTo == null)
                return Matrix4x4.TRS(handOffset, localQuaternionOffset, globalHandScale);

            // Con referencia: convertimos el offset local (respecto al objeto) a posición/rotación de mundo.
            Vector3 globalHandPosition = relativeTo.TransformPoint(handOffset);
            Quaternion globalHandRotation = relativeTo.rotation * localQuaternionOffset;
            return Matrix4x4.TRS(globalHandPosition, globalHandRotation, globalHandScale);
        }

        /// <summary>Escribe en esta pose la interpolación entre <paramref name="from"/> y <paramref name="to"/> por <paramref name="point"/> (0..1).</summary>
        public void LerpPose(ref HandPoseData from, ref HandPoseData to, float point) {
            // Interpolamos los offsets de la mano (posición, escala y rotación).
            handOffset = Vector3.Lerp(from.handOffset, to.handOffset, point);
            globalHandScale = Vector3.Lerp(from.globalHandScale, to.globalHandScale, point);
            localQuaternionOffset = Quaternion.Lerp(from.localQuaternionOffset, to.localQuaternionOffset, point);

            if (fingerPoses == null || fingerPoses.Length < 5)
                fingerPoses = new FingerPoseData[5];

            // Para cada dedo: partimos de la pose 'from' y la interpolamos hacia 'to'.
            for (int i = 0; i < 5; i++) {
                fingerPoses[i].CopyFromData(ref from.fingerPoses[i]);
                fingerPoses[i].LerpDataTo(ref to.fingerPoses[i], point);
            }
        }

        /// <summary>Escribe en <paramref name="result"/> la interpolación entre <paramref name="from"/> y <paramref name="to"/> por <paramref name="point"/> (0..1).</summary>
        public static void LerpPose(ref HandPoseData result, ref HandPoseData from, ref HandPoseData to, float point) {
            // Versión estática: igual que la de instancia, pero el resultado se escribe en 'result'
            // (útil para reutilizar una pose "scratch" sin reservar memoria).
            result.handOffset = Vector3.Lerp(from.handOffset, to.handOffset, point);
            result.globalHandScale = Vector3.Lerp(from.globalHandScale, to.globalHandScale, point);
            result.localQuaternionOffset = Quaternion.Lerp(from.localQuaternionOffset, to.localQuaternionOffset, point);

            if (result.fingerPoses == null || result.fingerPoses.Length < 5)
                result.fingerPoses = new FingerPoseData[5];

            for (int i = 0; i < 5; i++) {
                result.fingerPoses[i].CopyFromData(ref from.fingerPoses[i]);
                result.fingerPoses[i].LerpDataTo(ref to.fingerPoses[i], point);
            }
        }
    }
}
