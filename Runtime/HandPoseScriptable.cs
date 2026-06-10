using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Asset de pose de mano reutilizable. Guarda una <see cref="HandPoseData"/> izquierda y otra
    /// derecha, de modo que la misma pose se pueda compartir entre varios grabbables. Se crea con
    /// <c>Create &gt; Procedural Hands &gt; Custom Pose</c>.
    /// </summary>
    [CreateAssetMenu(fileName = "Hand Pose", menuName = "Procedural Hands/Custom Pose", order = 1)]
    public class HandPoseScriptable : ScriptableObject {

        // Flags ocultos que indican si cada lado tiene una pose guardada.
        [HideInInspector] public bool rightSaved;
        [HideInInspector] public bool leftSaved;

        [Label("Pose derecha", "Pose guardada para la mano derecha.")]
        public HandPoseData rightPose;
        [Label("Pose izquierda", "Pose guardada para la mano izquierda.")]
        public HandPoseData leftPose;

        /// <summary>Guarda ambas poses (izquierda y derecha).</summary>
        public void SavePoses(HandPoseData right, HandPoseData left) {
            SaveRightPose(right);
            SaveLeftPose(left);
        }

        /// <summary>Guarda la pose de la mano derecha.</summary>
        public void SaveRightPose(HandPoseData right) {
            // Copia profunda para no compartir los arrays internos con el origen.
            rightPose = new HandPoseData(ref right);
            rightSaved = true;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this); // marca el asset como modificado para que se guarde
#endif
        }

        /// <summary>Guarda la pose de la mano izquierda.</summary>
        public void SaveLeftPose(HandPoseData left) {
            leftPose = new HandPoseData(ref left);
            leftSaved = true;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
    }
}
