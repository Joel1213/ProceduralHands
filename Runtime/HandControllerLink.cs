using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Clase base que enlaza un mando con una <see cref="Hand"/>. Guarda referencias estáticas a los
    /// enlaces izquierdo/derecho activos (se usan para la háptica) y un punto de entrada virtual para
    /// la vibración. El input concreto lo aporta una subclase como <see cref="XRHandControllerLink"/>.
    /// </summary>
    public class HandControllerLink : MonoBehaviour {

        /// <summary>Los enlaces de mando izquierdo/derecho activos (los usa la háptica para encontrar el dispositivo).</summary>
        public static HandControllerLink handLeft, handRight;

        [Label("Mano", "La mano que controla este mando. Se resuelve sola desde este GameObject si se deja vacío.")]
        public Hand hand;

        protected virtual void Awake() {
            // Si no se asignó la mano en el inspector, la tomamos de este mismo GameObject.
            if (hand == null)
                hand = GetComponent<Hand>();
        }

        /// <summary>Reproduce un pulso háptico en este mando, si lo soporta. Se sobrescribe según la plataforma.</summary>
        public virtual void TryHapticImpulse(float duration, float amplitude, float frequency = 10f) { }
    }
}
