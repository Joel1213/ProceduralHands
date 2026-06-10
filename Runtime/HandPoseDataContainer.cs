using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Componente base que almacena una <see cref="HandPoseData"/> izquierda y otra derecha (inline o
    /// en un <see cref="HandPoseScriptable"/> compartido). Es el almacenamiento común de todos los
    /// componentes de pose de grabbable y expone helpers de guardado/consulta más los puntos de
    /// entrada de autoría en el editor.
    /// </summary>
    public class HandPoseDataContainer : MonoBehaviour {

        // Poses guardadas (ocultas: se editan con el Hand Pose Tool, no a mano en el inspector).
        [HideInInspector, SerializeField] public HandPoseData rightPose;
        [HideInInspector] public bool rightPoseSet;
        [HideInInspector, SerializeField] public HandPoseData leftPose;
        [HideInInspector] public bool leftPoseSet;

        [Label("Nombre de la pose", "Etiqueta organizativa para esta pose (solo informativa en el editor).")]
        public string poseName = "";
        [Label("Índice de pose", "Una pose solo la pueden usar manos que compartan este índice.")]
        public int poseIndex = 0;

        [Label("Pose scriptable", "Asset de pose compartido opcional. Si se asigna, las poses se leen/escriben en él en vez de inline.")]
        public HandPoseScriptable poseScriptable;

#if UNITY_EDITOR
        // Mano usada para autorar la pose en el editor (se asigna desde el inspector del componente).
        [HideInInspector] public Hand editorHand;
#endif

        /// <summary>Devuelve una referencia a la pose izquierda o derecha (del scriptable si está asignado).</summary>
        public virtual ref HandPoseData GetHandPoseData(bool left) {
            // Si hay un asset compartido, las poses viven ahí.
            if (poseScriptable != null) {
                if (left) return ref poseScriptable.leftPose;
                return ref poseScriptable.rightPose;
            }
            // Si no, usamos las poses inline de este componente.
            if (left) return ref leftPose;
            return ref rightPose;
        }

        /// <summary>Indica si el lado pedido tiene una pose guardada.</summary>
        public bool HasPose(bool left) {
            if (poseScriptable != null)
                return left ? poseScriptable.leftSaved : poseScriptable.rightSaved;
            return left ? leftPoseSet : rightPoseSet;
        }

        /// <summary>Guarda la forma actual de <paramref name="hand"/> en este contenedor.</summary>
        public virtual void SaveHandPose(Hand hand) {
            if (hand.left) {
                leftPose = new HandPoseData(hand, transform);
                leftPoseSet = true;
            }
            else {
                rightPose = new HandPoseData(hand, transform);
                rightPoseSet = true;
            }
        }

#if UNITY_EDITOR
        /// <summary>Editor: guarda la pose de la mano y la replica en el scriptable si hay uno asignado.</summary>
        public void EditorSaveGrabPose(Hand hand) {
            if (hand.left) {
                leftPose = new HandPoseData(hand, transform);
                leftPoseSet = true;
                if (poseScriptable != null)
                    poseScriptable.SaveLeftPose(leftPose);
            }
            else {
                rightPose = new HandPoseData(hand, transform);
                rightPoseSet = true;
                if (poseScriptable != null)
                    poseScriptable.SaveRightPose(rightPose);
            }
            UnityEditor.EditorUtility.SetDirty(this);
        }

        /// <summary>Editor: borra ambas poses guardadas.</summary>
        public void EditorClearPoses() {
            leftPoseSet = false;
            leftPose = new HandPoseData();
            rightPoseSet = false;
            rightPose = new HandPoseData();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        protected virtual void OnDrawGizmosSelected() {
            // Dibuja un gizmo sencillo de cada pose guardada (azul = derecha, rojo = izquierda).
            if (rightPoseSet)
                DrawPoseGizmo(ref rightPose, new Color(0.3f, 0.5f, 1f));
            if (leftPoseSet)
                DrawPoseGizmo(ref leftPose, new Color(1f, 0.4f, 0.4f));
        }

        // Dibuja una esfera en la posición de la mano de la pose y una línea hacia su "forward".
        void DrawPoseGizmo(ref HandPoseData pose, Color color) {
            Gizmos.color = color;
            Matrix4x4 m = pose.GetHandToWorldMatrix(transform);
            Vector3 pos = HandUtils.ExtractPosition(ref m);
            Quaternion rot = HandUtils.ExtractRotation(ref m);
            Gizmos.DrawWireSphere(pos, 0.02f);
            Gizmos.DrawLine(pos, pos + rot * Vector3.forward * 0.05f);
        }
    }
}
