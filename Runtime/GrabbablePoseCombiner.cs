using System.Collections.Generic;
using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Recoge los componentes <see cref="GrabbablePose"/> de un grabbable y elige aquel cuya
    /// colocación de mano guardada está más cerca de dónde está realmente la mano en el momento del
    /// agarre (ponderado por los pesos de posición/rotación de cada pose). Lo añade automáticamente
    /// <see cref="GrabbableBase"/>.
    /// </summary>
    public class GrabbablePoseCombiner : MonoBehaviour {

        [Label("Poses", "Las poses (GrabbablePose) de este grabbable entre las que elegir la más cercana.")]
        public List<GrabbablePose> poses = new List<GrabbablePose>();

        /// <summary>True si alguna de las poses contenidas puede aplicarla esta mano ahora mismo.</summary>
        public bool CanSetPose(Hand hand, Grabbable grab) {
            foreach (var pose in poses)
                if (pose != null && pose.CanSetPose(hand, grab))
                    return true;
            return false;
        }

        /// <summary>Registra una pose (se ignora si ya está).</summary>
        public void AddPose(GrabbablePose pose) {
            if (pose != null && !poses.Contains(pose))
                poses.Add(pose);
        }

        /// <summary>Número de poses contenidas.</summary>
        public int PoseCount() => poses.Count;

        /// <summary>Devuelve la pose usable más cercana a la posición/rotación actual de la mano.</summary>
        public GrabbablePose GetClosestPose(Hand hand, Grabbable grab) {
            // Guardamos la pose actual de la mano para restaurarla al final (las poses avanzadas la mueven).
            var handPosition = hand.transform.position;
            var handRotation = hand.transform.rotation;

            float closestValue = float.MaxValue;
            GrabbablePose chosen = null;

            foreach (var pose in poses) {
                if (pose == null || !pose.CanSetPose(hand, grab))
                    continue;

                // Ojo: las poses avanzadas calculan sus datos moviendo la mano un instante; se restaura
                // abajo con SyncTransforms para que la comparación de cercanía siga siendo válida.
                var data = pose.GetHandPoseData(hand);
                // Posición/rotación de mundo a las que la pose colocaría la mano.
                var globalPosition = pose.transform.TransformPoint(data.handOffset);
                var globalRotation = pose.transform.rotation * data.localQuaternionOffset;

                // "Cercanía" = distancia (ponderada por el peso de posición) + ángulo (ponderado por el de rotación).
                var distance = Vector3.Distance(globalPosition, handPosition);
                var angleDistance = Quaternion.Angle(globalRotation, handRotation) / 270f;
                var value = distance / Mathf.Max(0.0001f, pose.positionWeight) + angleDistance / Mathf.Max(0.0001f, pose.rotationWeight);

                // Nos quedamos con la de menor valor.
                if (value < closestValue) {
                    closestValue = value;
                    chosen = pose;
                }
            }

            // Restauramos la mano a su pose original (deshacemos cualquier movimiento de las poses avanzadas).
            hand.transform.SetPositionAndRotation(handPosition, handRotation);
            Physics.SyncTransforms();
            return chosen;
        }

        void OnDestroy() {
            // Al destruirse, eliminamos también las poses que gestiona (son componentes del mismo objeto).
            for (int i = poses.Count - 1; i >= 0; i--)
                if (poses[i] != null)
                    Destroy(poses[i]);
        }
    }
}
