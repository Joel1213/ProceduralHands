using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;                      // XRInteractionManager
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;  // IInteractionCaster

namespace ProceduralHands {
    /// <summary>
    /// PUENTE entre ProceduralHands y el sistema de interacción nativo de XRI. Reemplaza al
    /// <c>SphereInteractionCaster</c>: en vez de crear su propia esfera, se apoya en la esfera de
    /// detección de agarro de la mano (la del <see cref="GrabbableHighlighter"/>, cuyo tamaño se ajusta
    /// con "Distancia de alcance" en <see cref="Hand"/>) y le pasa al Near-Far Interactor el objeto que
    /// la mano detecta/agarra. Así los dos sistemas trabajan sobre el MISMO objeto: lo que la mano puede
    /// agarrar es lo que XRI puede interactuar, sin dos esferas que se desincronicen.
    ///
    /// Cómo: se suscribe al evento <see cref="GrabbableHighlighter.OnHighlight"/> (la mano detecta un
    /// grabbable) y guarda ese objeto como objetivo de XRI; lo deja de pasar cuando el objeto sale del
    /// rango SIN estar agarrándolo, o cuando se SUELTA con el mando (<see cref="Hand.OnReleased"/>) — NO
    /// al perder el resaltado por agarrar. Así, agarrar→soltar→reagarrar sin que el objeto salga de la
    /// esfera no rompe la interacción.
    ///
    /// Implementa <see cref="IInteractionCaster"/>: ponlo en el GameObject que tenía el
    /// SphereInteractionCaster (quita ese), asígnale la mano, y déjalo como "Near Interaction Caster"
    /// del Near-Far Interactor (vacío → lo auto-detecta; o arrástralo al campo).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Procedural Hands/Hand Interaction Caster")]
    public class HandInteractionCaster : MonoBehaviour, IInteractionCaster {

        [SerializeField]
        [Tooltip("Mano procedural cuya detección de agarro alimenta a XRI (su highlighter y su Distancia de alcance).")]
        Hand m_Hand;

        /// <summary>Mano cuya detección de agarro usa este puente.</summary>
        public Hand hand { get => m_Hand; set => m_Hand = value; }

        // Objeto que la mano detecta/agarra ahora mismo = el que XRI debe ver. Lo fijan los eventos.
        Grabbable m_Target;
        bool m_Subscribed;

        // --- IInteractionCaster (lo que el Near-Far Interactor espera) ---

        /// <inheritdoc />
        public bool isInitialized => m_Hand != null;

        /// <summary>El origen es la palma (informativo: el interactor lo usa para ordenar por distancia).</summary>
        public Transform castOrigin {
            get => m_Hand != null ? m_Hand.palmTransform : transform;
            set { /* ignorado: aquí no usamos un origen externo */ }
        }

        /// <inheritdoc />
        public Transform effectiveCastOrigin => castOrigin;

        void OnEnable() => Subscribe();
        void OnDisable() => Unsubscribe();

        void Subscribe() {
            if (m_Subscribed || m_Hand == null)
                return;
            m_Hand.highlighter.OnHighlight += HandleHighlight;
            m_Hand.highlighter.OnStopHighlight += HandleStopHighlight;
            m_Hand.OnReleased += HandleReleased;
            m_Subscribed = true;
        }

        void Unsubscribe() {
            if (!m_Subscribed)
                return;
            if (m_Hand != null) {
                m_Hand.highlighter.OnHighlight -= HandleHighlight;
                m_Hand.highlighter.OnStopHighlight -= HandleStopHighlight;
                m_Hand.OnReleased -= HandleReleased;
            }
            m_Subscribed = false;
            m_Target = null;
        }

        // La mano detecta un grabbable agarrable: es el objeto que XRI debe ver.
        void HandleHighlight(Hand h, Grabbable grab) {
            m_Target = grab;
        }

        // La mano deja de resaltar. Solo lo soltamos si NO está agarrando (= el objeto se fue del rango).
        // Si está agarrando, mantenemos el objetivo para que XRI conserve la interacción mientras se
        // sostiene (el highlighter no dispara esto al agarrar, pero lo comprobamos por seguridad).
        void HandleStopHighlight(Hand h, Grabbable grab) {
            if (!h.IsGrabbing() && h.holdingObj == null)
                m_Target = null;
        }

        // Se soltó con el mando: AQUÍ es donde vaciamos la referencia (no al salir del rango).
        void HandleReleased(Hand h, Grabbable grab) {
            m_Target = null;
        }

        /// <inheritdoc />
        public bool TryGetColliderTargets(XRInteractionManager interactionManager, List<Collider> targets) {
            targets.Clear();
            if (m_Hand == null)
                return false;

            // Preferimos lo que la mano SOSTIENE: se fija al instante al agarrar (no depende del
            // highlighter, que va a ~30 Hz y se apaga al agarrar), así que cubre agarrar/reagarrar tan
            // rápido que OnHighlight no llegó a dispararse. Si no sostiene nada, usamos lo último que
            // detectó el highlighter para el hover previo.
            Grabbable obj = m_Hand.holdingObj != null ? m_Hand.holdingObj : m_Target;
            if (obj == null)
                return false;

            var cols = obj.GrabColliders;
            for (int i = 0; i < cols.Count; i++)
                if (cols[i] != null)
                    targets.Add(cols[i]);
            return targets.Count > 0;
        }

        void OnDrawGizmosSelected() {
            if (m_Hand == null || m_Hand.palmTransform == null)
                return;
            // La esfera de detección de la mano (la del highlighter), solo como ayuda visual.
            Gizmos.color = new Color(0f, 0.7f, 1f, 0.9f);
            Gizmos.DrawWireSphere(
                m_Hand.palmTransform.position + m_Hand.palmTransform.forward * (m_Hand.reachDistance / 3f),
                m_Hand.reachDistance);
        }
    }
}
