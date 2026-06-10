using UnityEditor;
using UnityEngine;

namespace ProceduralHands.EditorTools {
    /// <summary>
    /// Inspector de <see cref="Finger"/>: valida la asignación de articulaciones/punta y el tipo de
    /// dedo, muestra qué poses están guardadas y ofrece previsualizaciones de abierto/cerrado. Los
    /// huesos se asignan manualmente (con etiquetas en escena), según el flujo "física + componentes"
    /// del wizard.
    /// </summary>
    [CustomEditor(typeof(Finger))]
    [CanEditMultipleObjects]
    public class FingerEditor : Editor {

        /// <summary>Dibuja el inspector por defecto más las ayudas de validación y los botones de previsualización.</summary>
        public override void OnInspectorGUI() {
            // Primero el inspector estándar (con nuestras etiquetas en español vía [Label]).
            DrawDefaultInspector();

            // Obtenemos el dedo objetivo; si no es un Finger válido, no añadimos nada.
            var finger = target as Finger;
            if (finger == null)
                return;

            EditorGUILayout.Space();

            // Aviso si falta el tipo de dedo (sin él, la mano no sabe a qué dedo corresponde).
            if (finger.fingerType == FingerEnum.none)
                EditorGUILayout.HelpBox("Asigna el tipo de dedo (índice, corazón, anular, meñique, pulgar).", MessageType.Error);
            // Aviso si faltan articulaciones por asignar.
            if (finger.isMissingReferences)
                EditorGUILayout.HelpBox("Asigna todas las articulaciones: nudillo, media, distal y punta.", MessageType.Error);

            // Mostramos qué poses (abierta/cerrada) tiene guardadas este dedo.
            EditorGUILayout.LabelField("Poses guardadas", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Abierta: {finger.IsPoseSaved(FingerPoseEnum.Open)}    Cerrada: {finger.IsPoseSaved(FingerPoseEnum.Closed)}");

            // Los botones de previsualización solo se habilitan si hay referencias completas y ambas poses guardadas
            // (sin ellas, SetFingerBend no tendría datos entre los que interpolar).
            using (new EditorGUI.DisabledScope(finger.isMissingReferences
                || !finger.IsPoseSaved(FingerPoseEnum.Open)
                || !finger.IsPoseSaved(FingerPoseEnum.Closed))) {
                EditorGUILayout.BeginHorizontal();
                // Bend 0 = totalmente abierto.
                if (GUILayout.Button("Previsualizar abierto"))
                    finger.SetFingerBend(0f);
                // Bend 1 = totalmente cerrado.
                if (GUILayout.Button("Previsualizar cerrado"))
                    finger.SetFingerBend(1f);
                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>Dibuja etiquetas en la escena sobre cada articulación del dedo para ayudar a asignarlas.</summary>
        void OnSceneGUI() {
            var finger = target as Finger;
            // Sin dedo válido o con referencias incompletas no hay nada que etiquetar.
            if (finger == null || finger.isMissingReferences)
                return;

            // Etiquetamos cada articulación en su posición en mundo.
            Handles.color = Color.cyan;
            Label(finger.knuckleJoint, "nudillo");
            Label(finger.middleJoint, "media");
            Label(finger.distalJoint, "distal");
            Label(finger.tip, "punta");
        }

        // Dibuja una etiqueta de texto en la posición del transform, si existe.
        static void Label(Transform t, string text) {
            if (t != null)
                Handles.Label(t.position, text);
        }
    }
}
