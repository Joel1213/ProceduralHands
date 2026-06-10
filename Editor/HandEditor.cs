using UnityEditor;
using UnityEngine;

namespace ProceduralHands.EditorTools {
    /// <summary>
    /// Inspector de <see cref="Hand"/>: añade botones para autorar poses (Guardar Abierta/Cerrada/Pinza,
    /// coloreados según si todos los dedos tienen ya esa pose), además de utilidades de copiar/espejar.
    /// </summary>
    [CustomEditor(typeof(Hand))]
    [CanEditMultipleObjects]
    public class HandEditor : Editor {

        // Poses que se pueden autorar desde este inspector (abierta, cerrada y las dos de pinza).
        static readonly FingerPoseEnum[] AuthorablePoses = {
            FingerPoseEnum.Open, FingerPoseEnum.Closed, FingerPoseEnum.PinchOpen, FingerPoseEnum.PinchClosed
        };

        /// <summary>Dibuja el inspector por defecto más la sección de autorado de poses y las utilidades.</summary>
        public override void OnInspectorGUI() {
            DrawDefaultInspector();

            var hand = target as Hand;
            if (hand == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Autorado de poses", EditorStyles.boldLabel);

            // Sin dedos asignados no se pueden guardar poses: avisamos.
            if (hand.fingers == null || hand.fingers.Length == 0) {
                EditorGUILayout.HelpBox("Asigna los componentes Finger antes de guardar poses.", MessageType.Info);
            }
            else {
                // Un botón por pose; en verde si TODOS los dedos ya tienen esa pose, en rojo claro si falta alguno.
                Color prev = GUI.backgroundColor;
                foreach (var pose in AuthorablePoses) {
                    GUI.backgroundColor = AllFingersHavePose(hand, pose) ? Color.green : new Color(1f, 0.6f, 0.6f);
                    if (GUILayout.Button("Guardar pose " + pose))
                        SaveAll(hand, pose);
                }
                // Restauramos el color de fondo de la GUI.
                GUI.backgroundColor = prev;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Utilidades", EditorStyles.boldLabel);
            // Copiar datos de pose desde otra mano: solo habilitado si se asignó 'copyFromHand'.
            using (new EditorGUI.DisabledScope(hand.copyFromHand == null)) {
                if (GUILayout.Button("Copiar datos de pose desde 'copyFromHand'"))
                    CopyData(hand);
            }
            // Espejar la mano (invierte X y cambia de lado izquierda/derecha).
            if (GUILayout.Button("Espejar mano (invertir X / cambiar lado)"))
                MirrorHand(hand);
        }

        // Devuelve true solo si TODOS los dedos tienen guardada la pose indicada.
        static bool AllFingersHavePose(Hand hand, FingerPoseEnum pose) {
            foreach (var finger in hand.fingers)
                if (finger == null || !finger.IsPoseSaved(pose))
                    return false;
            return true;
        }

        // Guarda la pose indicada (la postura actual) en todos los dedos de la mano.
        static void SaveAll(Hand hand, FingerPoseEnum pose) {
            foreach (var finger in hand.fingers) {
                if (finger == null)
                    continue;
                // Guardamos la postura actual del dedo como esa pose y lo marcamos sucio para que Unity persista el cambio.
                finger.SavePose(hand, finger, pose);
                EditorUtility.SetDirty(finger);
            }
            Debug.Log($"Procedural Hands: guardada la pose {pose} de {hand.name}.", hand);
        }

        // Copia los datos de pose de cada dedo desde la mano 'copyFromHand' (emparejando dedo a dedo por índice).
        static void CopyData(Hand hand) {
            if (hand.copyFromHand == null || hand.fingers == null)
                return;
            // Recorremos limitando al menor número de dedos de ambas manos para no salirnos de rango.
            for (int i = 0; i < hand.fingers.Length && i < hand.copyFromHand.fingers.Length; i++) {
                if (hand.fingers[i] == null || hand.copyFromHand.fingers[i] == null)
                    continue;
                hand.fingers[i].CopyPoseData(hand.copyFromHand.fingers[i]);
                EditorUtility.SetDirty(hand.fingers[i]);
            }
            Debug.Log("Procedural Hands: datos de pose copiados.", hand);
        }

        // Espeja la mano: invierte la escala X, voltea la matriz base de cada pose y cambia el lado (left).
        static void MirrorHand(Hand hand) {
            // Registramos el transform para poder deshacer el espejado.
            Undo.RecordObject(hand.transform, "Mirror Hand");
            // Invertimos la escala en X (espejo respecto al plano YZ).
            var scale = hand.transform.localScale;
            scale.x = -scale.x;
            hand.transform.localScale = scale;

            if (hand.fingers != null) {
                foreach (var finger in hand.fingers) {
                    if (finger == null || finger.poseData == null)
                        continue;
                    // Para cada pose guardada del dedo, volteamos la componente m00 de su primera matriz relativa
                    // (esto espeja en X la rotación/posición base de la pose para que case con la mano invertida).
                    for (int i = 0; i < finger.poseData.Length; i++) {
                        if (!finger.poseData[i].isSet || finger.poseData[i].poseRelativeMatrix.Length == 0)
                            continue;
                        var m = finger.poseData[i].poseRelativeMatrix[0];
                        m.m00 *= -1;
                        finger.poseData[i].poseRelativeMatrix[0] = m;
                    }
                    EditorUtility.SetDirty(finger);
                }
            }

            // Cambiamos el lado de la mano y la marcamos sucia.
            hand.left = !hand.left;
            EditorUtility.SetDirty(hand);
        }
    }
}
