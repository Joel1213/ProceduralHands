using System.Collections.Generic;
using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Pose personalizada fija para un grabbable: guarda la colocación exacta de la mano y la forma de
    /// los dedos que se usan al agarrar el objeto. Puede haber varias poses en un mismo grabbable y el
    /// <see cref="GrabbablePoseCombiner"/> elige la más cercana. Se autoran con el Hand Pose Tool.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class GrabbablePose : HandPoseDataContainer {

        [Label("Pose habilitada", "Si esta pose se puede usar ahora mismo.")]
        public bool poseEnabled = true;
        [Label("Una sola mano", "Si está activo, solo una mano puede usar esta pose a la vez.")]
        public bool singleHanded = false;

        [Header("Pesos de selección")]
        [Label("Peso de posición", "Mayor = esta pose se prefiere por posición al elegir la más cercana.")]
        public float positionWeight = 1f;
        [Label("Peso de rotación", "Mayor = esta pose se prefiere por rotación al elegir la más cercana.")]
        public float rotationWeight = 1f;

        [Label("Poses enlazadas", "Estas poses solo se habilitan mientras esta pose está activa (p. ej. la pose de la segunda mano).")]
        public GrabbablePose[] linkedPoses = new GrabbablePose[0];

        /// <summary>El grabbable dueño de esta pose (lo asigna GrabbableBase).</summary>
        public Grabbable grabbable { get; internal set; }
        /// <summary>Las manos que están usando esta pose ahora mismo.</summary>
        public List<Hand> posingHands { get; protected set; } = new List<Hand>();

        protected virtual void Awake() {
            posingHands = new List<Hand>();
            // Si usamos un asset compartido, marcamos qué lados tienen pose guardada.
            if (poseScriptable != null) {
                if (poseScriptable.leftSaved) leftPoseSet = true;
                if (poseScriptable.rightSaved) rightPoseSet = true;
            }
            // Las poses enlazadas arrancan deshabilitadas; se habilitan solo cuando esta pose se activa.
            for (int i = 0; i < linkedPoses.Length; i++)
                if (linkedPoses[i] != null)
                    linkedPoses[i].poseEnabled = false;
        }

        /// <summary>Indica si <paramref name="hand"/> puede aplicar esta pose a <paramref name="grab"/> ahora mismo.</summary>
        public bool CanSetPose(Hand hand, Grabbable grab) {
            // Si es de una sola mano y ya la usa otra (y no se permite intercambio), no se puede.
            if (singleHanded && posingHands.Count > 0 && !posingHands.Contains(hand) && !(grab.singleHandOnly && grab.allowHeldSwapping))
                return false;
            // El índice de pose debe coincidir con el de la mano.
            if (hand.poseIndex != poseIndex)
                return false;
            // Debe existir una pose guardada para el lado de esta mano.
            if (hand.left && !HasPose(true))
                return false;
            if (!hand.left && !HasPose(false))
                return false;
            return poseEnabled;
        }

        /// <summary>Devuelve los datos de pose para el lado de la mano dada.</summary>
        public virtual ref HandPoseData GetHandPoseData(Hand hand) {
            return ref GetHandPoseData(hand.left);
        }

        /// <summary>Aplica esta pose (posición + dedos) a <paramref name="hand"/>.</summary>
        /// <param name="isProjection">Si es true, no registra la mano ni habilita las poses enlazadas.</param>
        public virtual void SetHandPose(Hand hand, bool isProjection = false) {
            if (!isProjection) {
                // Registramos la mano como "posando" y habilitamos las poses enlazadas.
                if (!posingHands.Contains(hand))
                    posingHands.Add(hand);
                for (int i = 0; i < linkedPoses.Length; i++)
                    if (linkedPoses[i] != null)
                        linkedPoses[i].poseEnabled = true;
            }
            // Aplicamos la pose (posición + dedos) relativa a este transform.
            GetHandPoseData(hand).SetPose(hand, transform);
        }

        /// <summary>Libera a <paramref name="hand"/> de esta pose y deshabilita sus poses enlazadas.</summary>
        public virtual void CancelHandPose(Hand hand) {
            if (posingHands.Contains(hand))
                posingHands.Remove(hand);
            for (int i = 0; i < linkedPoses.Length; i++)
                if (linkedPoses[i] != null)
                    linkedPoses[i].poseEnabled = false;
        }
    }
}
