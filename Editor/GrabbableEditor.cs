using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ProceduralHands.EditorTools {
    /// <summary>
    /// Inspector de <see cref="Grabbable"/>: atajos para añadir componentes de pose personalizada y una
    /// pista de cómo autorarlas con el Hand Pose Tool. Además, cuando el tipo de pose de agarre es
    /// <c>Climb</c>, oculta los campos de física/sujeción/lanzamiento que no se usan y muestra un aviso.
    /// </summary>
    [CustomEditor(typeof(Grabbable))]
    [CanEditMultipleObjects]
    public class GrabbableEditor : Editor {

        // Campos de física/sujeción/lanzamiento que NO tienen efecto en modo Climb (se ocultan para no confundir).
        static readonly HashSet<string> climbHiddenFields = new HashSet<string> {
            "instantGrab", "parentOnGrab",
            "heldNoFriction", "minHeldDrag", "minHeldAngleDrag", "minHeldMass",
            "maxHeldVelocity", "heldPositionOffset", "heldRotationOffset",
            "throwPower", "jointBreakForce", "ignoreReleaseTime"
        };

        /// <summary>Dibuja el inspector (ocultando campos no usados en Climb) más la sección de poses personalizadas.</summary>
        public override void OnInspectorGUI() {
            serializedObject.Update();

            // ¿El tipo de pose de agarre es Climb? De eso depende qué campos mostramos.
            var poseTypeProp = serializedObject.FindProperty("grabPoseType");
            bool isClimb = poseTypeProp != null && !poseTypeProp.hasMultipleDifferentValues
                && poseTypeProp.enumValueIndex == (int)HandGrabPoseType.Climb;

            // Recorremos todas las propiedades visibles y las dibujamos, saltando las de física cuando es Climb.
            var prop = serializedObject.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren)) {
                enterChildren = false;
                // m_Script es el campo del script en sí: no se edita.
                if (prop.name == "m_Script")
                    continue;
                // En Climb ocultamos los campos que no aplican (y sus headers asociados desaparecen con ellos).
                if (isClimb && climbHiddenFields.Contains(prop.name))
                    continue;
                EditorGUILayout.PropertyField(prop, true);
            }
            serializedObject.ApplyModifiedProperties();

            // Aviso explicativo del modo Climb.
            if (isClimb)
                EditorGUILayout.HelpBox(
                    "Modo Climb (escalada): el asidero debe tener su Rigidbody en 'Is Kinematic'. No se crea joint ni se mueve el objeto; " +
                    "la mano se posa y se ancla al asidero, y la escalada la gestiona el sistema XRI (ClimbInteractable + ClimbProvider). " +
                    "Por eso se ocultan los ajustes de sujeción y lanzamiento. Opcional: añade una pose (normal o avanzada) para dar forma al agarre.",
                    MessageType.Info);

            var grab = target as Grabbable;
            if (grab == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Poses personalizadas", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Añade una pose, asígnale su 'Mano de editor' y usa 'Crear copia de mano y abrir Pose Tool' en el componente de pose para autorarla. " +
                "Para agarrar un tubo a lo largo de su eje (típico al escalar), usa la pose avanzada.",
                MessageType.Info);

            // Botones para añadir una pose normal o avanzada al objeto (con soporte de Undo).
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Añadir pose de agarre"))
                Undo.AddComponent<GrabbablePose>(grab.gameObject);
            if (GUILayout.Button("Añadir pose avanzada"))
                Undo.AddComponent<GrabbablePoseAdvanced>(grab.gameObject);
            EditorGUILayout.EndHorizontal();

            // Listamos (en solo lectura) las poses que ya tiene este objeto.
            var poses = grab.GetComponents<GrabbablePose>();
            if (poses.Length > 0) {
                EditorGUILayout.LabelField($"Poses en este objeto: {poses.Length}");
                using (new EditorGUI.DisabledScope(true)) {
                    foreach (var pose in poses)
                        EditorGUILayout.ObjectField(pose, typeof(GrabbablePose), true);
                }
            }
        }
    }
}
