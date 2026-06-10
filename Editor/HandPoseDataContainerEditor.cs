using UnityEditor;
using UnityEngine;

namespace ProceduralHands.EditorTools {
    /// <summary>
    /// Inspector compartido por todo contenedor de poses (<see cref="GrabbablePose"/>,
    /// <see cref="GrabbablePoseAdvanced"/>). Permite asignar una "mano de editor", generar una copia
    /// posable de ella en la pose, abrir el Hand Pose Tool y borrar las poses guardadas.
    /// </summary>
    [CustomEditor(typeof(HandPoseDataContainer), true)]
    [CanEditMultipleObjects]
    public class HandPoseDataContainerEditor : Editor {

        /// <summary>Dibuja el inspector por defecto más los controles de autorado de la pose.</summary>
        public override void OnInspectorGUI() {
            DrawDefaultInspector();

            var pose = target as HandPoseDataContainer;
            if (pose == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Autorado de pose", EditorStyles.boldLabel);

            // Mano de referencia que se clonará para posar (la "mano de editor").
            pose.editorHand = (Hand)EditorGUILayout.ObjectField("Mano de editor", pose.editorHand, typeof(Hand), true);
            // Mostramos qué lados (izquierda/derecha) tienen ya pose guardada.
            EditorGUILayout.LabelField($"Izquierda guardada: {pose.HasPose(true)}    Derecha guardada: {pose.HasPose(false)}");

            // Solo se puede crear la copia si hay una mano de editor asignada.
            using (new EditorGUI.DisabledScope(pose.editorHand == null)) {
                if (GUILayout.Button("Crear copia de mano y abrir Pose Tool"))
                    CreateHandCopy(pose);
            }

            // Borra las poses guardadas de este contenedor (con Undo).
            if (GUILayout.Button("Borrar poses")) {
                Undo.RecordObject(pose, "Clear Poses");
                pose.EditorClearPoses();
            }
        }

        /// <summary>Genera una copia de la mano de editor en la pose y abre el Hand Pose Tool sobre ella.</summary>
        public static void CreateHandCopy(HandPoseDataContainer pose) {
            var source = pose.editorHand;
            if (source == null)
                return;

            // Reutilizamos la copia si ya es la mano temporal; si no, instanciamos una nueva.
            Hand handCopy = source.name == "HAND COPY DELETE" ? source : Instantiate(source);
            handCopy.name = "HAND COPY DELETE";

            // Contenedor situado en la pose: nos permite espejar la copia escalando este padre.
            var container = new GameObject("HAND COPY CONTAINER DELETE");
            container.transform.SetPositionAndRotation(pose.transform.position, pose.transform.rotation);
            handCopy.transform.SetParent(container.transform, true);
            pose.editorHand = handCopy;

            // Si ya hay pose guardada para este lado, la aplicamos; si no, colocamos la mano en la pose y relajamos los dedos.
            bool left = handCopy.left;
            if (pose.HasPose(left))
                pose.GetHandPoseData(left).SetPose(handCopy, pose.transform);
            else {
                handCopy.transform.SetPositionAndRotation(pose.transform.position, pose.transform.rotation);
                RelaxFingers(handCopy);
            }

            // Registramos la creación para Undo, seleccionamos la copia y enfocamos la cámara de escena en ella.
            Undo.RegisterCreatedObjectUndo(container, "Create Hand Copy");
            Selection.activeGameObject = handCopy.gameObject;
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();

            // Abrimos la ventana de autorado de pose sobre la copia.
            HandPoseToolWindow.ShowWindow(handCopy, pose);
        }

        // Deja los dedos a media flexión (gripOffset) como punto de partida cómodo para empezar a posar.
        static void RelaxFingers(Hand hand) {
            if (hand.fingers == null)
                return;
            foreach (var finger in hand.fingers) {
                if (finger == null)
                    continue;
                // Solo si el dedo tiene ambas poses (abierta y cerrada) para poder interpolar entre ellas.
                if (finger.IsPoseSaved(FingerPoseEnum.Open) && finger.IsPoseSaved(FingerPoseEnum.Closed))
                    finger.SetFingerBend(hand.gripOffset);
            }
        }
    }
}
