using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Datos de pose serializables de un único dedo. La pose se guarda como las matrices TRS relativas
    /// entre articulaciones consecutivas (nudillo→media→distal→punta), lo que la hace independiente del
    /// transform de mundo de la mano. <see cref="localRotations"/> cachea la rotación relativa de cada
    /// articulación para que mezclar y aplicar poses no tenga que reconstruir las rotaciones desde las
    /// matrices en cada frame (sería más caro).
    /// </summary>
    [System.Serializable]
    public struct FingerPoseData {

        /// <summary>Matriz TRS de cada articulación respecto a su articulación padre (la mano, en el caso del nudillo).</summary>
        public Matrix4x4[] poseRelativeMatrix;

        /// <summary>Rotación relativa cacheada de cada articulación respecto a su padre. La reconstruye <see cref="CalculateAdditionalValues"/>.</summary>
        public Quaternion[] localRotations;

        /// <summary>True cuando ya se han calculado las rotaciones locales cacheadas.</summary>
        public bool isLocalSet => localRotations != null && localRotations.Length > 0;

        /// <summary>True cuando ya se han guardado las matrices relativas.</summary>
        public bool isSet => poseRelativeMatrix != null && poseRelativeMatrix.Length > 0;

        /// <summary>Crea los arrays internos si faltan (para reutilizar un struct por defecto sin reservar de más).</summary>
        public void Allocate() {
            // 4 entradas: nudillo, media, distal y punta.
            if (poseRelativeMatrix == null || poseRelativeMatrix.Length != 4)
                poseRelativeMatrix = new Matrix4x4[4];
            if (localRotations == null || localRotations.Length != 4)
                localRotations = new Quaternion[4];
        }

        /// <summary>Captura la forma actual de <paramref name="finger"/> relativa a <paramref name="hand"/>.</summary>
        public FingerPoseData(Hand hand, Finger finger) {
            poseRelativeMatrix = new Matrix4x4[4];
            localRotations = new Quaternion[4];
            SetPoseData(hand, finger);
        }

        /// <summary>Hace una copia profunda de otra pose de dedo.</summary>
        public FingerPoseData(ref FingerPoseData data) {
            poseRelativeMatrix = new Matrix4x4[4];
            localRotations = new Quaternion[4];
            CopyFromData(ref data);
        }

        /// <summary>Vuelve a capturar la forma actual de <paramref name="finger"/> en esta pose (reutilizando los arrays).</summary>
        public void SetPoseData(Hand hand, Finger finger) {
            Allocate();
            // Cada matriz relativa = "del mundo al espacio del padre" * "del espacio del hijo al mundo".
            // Resultado: la transformada del hijo expresada en el espacio local de su padre.
            // Nudillo respecto a la mano:
            poseRelativeMatrix[(int)FingerJointEnum.knuckle] = hand.transform.worldToLocalMatrix * finger.knuckleJoint.localToWorldMatrix;
            // Media respecto al nudillo:
            poseRelativeMatrix[(int)FingerJointEnum.middle] = finger.knuckleJoint.worldToLocalMatrix * finger.middleJoint.localToWorldMatrix;
            // Distal respecto a la media:
            poseRelativeMatrix[(int)FingerJointEnum.distal] = finger.middleJoint.worldToLocalMatrix * finger.distalJoint.localToWorldMatrix;
            // Punta respecto a la distal:
            poseRelativeMatrix[(int)FingerJointEnum.tip] = finger.distalJoint.worldToLocalMatrix * finger.tip.localToWorldMatrix;
            // Con las matrices listas, recalculamos las rotaciones locales cacheadas.
            CalculateAdditionalValues(hand.transform.lossyScale);
        }

        /// <summary>Copia profunda de las matrices y rotaciones cacheadas desde <paramref name="other"/>.</summary>
        public void CopyFromData(ref FingerPoseData other) {
            // Si el origen no tiene datos, no hay nada que copiar.
            if (!other.isSet)
                return;
            Allocate();
            other.poseRelativeMatrix.CopyTo(poseRelativeMatrix, 0);
            // Las rotaciones locales solo si el origen las tiene calculadas.
            if (other.isLocalSet)
                other.localRotations.CopyTo(localRotations, 0);
        }

        /// <summary>Copia profunda desde <paramref name="other"/> (sobrecarga por valor).</summary>
        public void CopyFromData(FingerPoseData other) {
            CopyFromData(ref other);
        }

        /// <summary>Interpola las rotaciones cacheadas de esta pose hacia <paramref name="other"/> por <paramref name="point"/> (0..1).</summary>
        /// <param name="updateMatrixData">Reconstruir también las matrices relativas desde las rotaciones mezcladas (solo hace falta al re-guardar).</param>
        public void LerpDataTo(ref FingerPoseData other, float point, bool updateMatrixData = false) {
            Allocate();
            // Interpolamos articulación a articulación entre nuestra rotación y la de 'other'.
            for (int i = 0; i < localRotations.Length; i++) {
                Quaternion interpolated = Quaternion.Lerp(localRotations[i], other.localRotations[i], point);
                localRotations[i] = interpolated;
                // Solo si nos lo piden, volcamos la rotación mezclada de vuelta a la matriz (conservando pos y escala).
                if (updateMatrixData)
                    poseRelativeMatrix[i].SetTRS(HandUtils.ExtractPosition(ref poseRelativeMatrix[i]), interpolated, HandUtils.ExtractScale(ref poseRelativeMatrix[i]));
            }
        }

        /// <summary>Escribe en esta pose la interpolación entre <paramref name="fromPose"/> y <paramref name="toPose"/> por <paramref name="point"/> (0..1).</summary>
        public void LerpData(ref FingerPoseData fromPose, ref FingerPoseData toPose, float point, bool updateMatrixData = false) {
            Allocate();
            // Para cada articulación, mezclamos la rotación de 'from' hacia la de 'to' y la guardamos aquí.
            for (int i = 0; i < localRotations.Length; i++) {
                Quaternion interpolated = Quaternion.Lerp(fromPose.localRotations[i], toPose.localRotations[i], point);
                localRotations[i] = interpolated;
                if (updateMatrixData)
                    poseRelativeMatrix[i].SetTRS(HandUtils.ExtractPosition(ref poseRelativeMatrix[i]), interpolated, HandUtils.ExtractScale(ref poseRelativeMatrix[i]));
            }
        }

        /// <summary>Aplica esta pose a las articulaciones del dedo usando la rotación de mundo actual de la mano.</summary>
        public void SetFingerPose(Finger finger) {
            // Sobrecarga cómoda: usa la rotación de la mano del propio dedo y sus tres articulaciones.
            SetFingerPose(finger.hand.transform.rotation, finger.knuckleJoint, finger.middleJoint, finger.distalJoint);
        }

        /// <summary>
        /// Aplica esta pose a las articulaciones dadas. Sobrecarga más rápida que evita repetir los getters
        /// de transform al mezclar; pásale la rotación de mundo de la mano y los tres transforms de las articulaciones.
        /// </summary>
        public void SetFingerPose(Quaternion handRotation, Transform knuckleJoint, Transform middleJoint, Transform distalJoint) {
            // Reconstruimos las rotaciones de mundo encadenando: cada articulación es la rotación de su
            // padre por su rotación relativa cacheada. Empezamos por la mano.
            Quaternion knuckleRotation = handRotation * localRotations[(int)FingerJointEnum.knuckle];
            Quaternion middleRotation = knuckleRotation * localRotations[(int)FingerJointEnum.middle];
            Quaternion distalRotation = middleRotation * localRotations[(int)FingerJointEnum.distal];

            // Aplicamos las rotaciones de mundo a los huesos (la punta no se rota: es solo un marcador).
            knuckleJoint.rotation = knuckleRotation;
            middleJoint.rotation = middleRotation;
            distalJoint.rotation = distalRotation;
        }

        /// <summary>
        /// Reconstruye <see cref="localRotations"/> (las rotaciones relativas por articulación) a partir de
        /// las matrices relativas guardadas. Hay que llamarlo una vez tras fijar o deserializar las matrices.
        /// </summary>
        public void CalculateAdditionalValues(Vector3 handLossyScale) {
            if (localRotations == null || localRotations.Length != 4)
                localRotations = new Quaternion[4];

            // Partimos de una matriz "mano" que solo aplica la escala (posición 0, rotación identidad):
            // así las rotaciones que extraigamos son independientes de dónde esté la mano en el mundo.
            Matrix4x4 handGlobalMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, handLossyScale);
            // Encadenamos las matrices relativas para obtener la transformada acumulada de cada articulación.
            Matrix4x4 knuckleGlobalMatrix = handGlobalMatrix * poseRelativeMatrix[(int)FingerJointEnum.knuckle];
            Matrix4x4 middleGlobalMatrix = knuckleGlobalMatrix * poseRelativeMatrix[(int)FingerJointEnum.middle];
            Matrix4x4 distalGlobalMatrix = middleGlobalMatrix * poseRelativeMatrix[(int)FingerJointEnum.distal];
            Matrix4x4 tipGlobalMatrix = distalGlobalMatrix * poseRelativeMatrix[(int)FingerJointEnum.tip];

            // Extraemos la rotación acumulada de cada articulación.
            Quaternion knuckleRotation = HandUtils.ExtractRotation(ref knuckleGlobalMatrix);
            Quaternion middleRotation = HandUtils.ExtractRotation(ref middleGlobalMatrix);
            Quaternion distalRotation = HandUtils.ExtractRotation(ref distalGlobalMatrix);
            Quaternion tipRotation = HandUtils.ExtractRotation(ref tipGlobalMatrix);

            // Guardamos la rotación RELATIVA de cada articulación respecto a su padre:
            // relativa = inversa(rotación del padre) * rotación del hijo.
            localRotations[(int)FingerJointEnum.knuckle] = knuckleRotation; // el "padre" del nudillo es la mano (rotación identidad aquí)
            localRotations[(int)FingerJointEnum.middle] = Quaternion.Inverse(knuckleRotation) * middleRotation;
            localRotations[(int)FingerJointEnum.distal] = Quaternion.Inverse(middleRotation) * distalRotation;
            localRotations[(int)FingerJointEnum.tip] = Quaternion.Inverse(distalRotation) * tipRotation;
        }

        /// <summary>Suma de las diferencias angulares por articulación frente a <paramref name="other"/>, en grados.</summary>
        public float GetPoseDifferenceByAngle(ref FingerPoseData other) {
            float angleDifference = 0;
            for (int i = 0; i < localRotations.Length; i++)
                angleDifference += Quaternion.Angle(localRotations[i], other.localRotations[i]);
            return angleDifference;
        }
    }
}
