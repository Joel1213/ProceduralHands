using System.Collections.Generic;
using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Ayudante simple (no es un componente) que registra la velocidad lineal y angular del objeto
    /// sostenido en una ventana temporal corta y las promedia para producir una velocidad de
    /// lanzamiento estable al soltar. Lo posee <see cref="HandBase"/>.
    /// </summary>
    public class HandVelocityTracker {

        readonly HandBase hand;
        // Umbral mínimo: por debajo de esta velocidad no se lanza (se considera ruido).
        readonly float minThrowVelocity = 0f;

        // Muestras recientes de velocidad lineal y angular del objeto sostenido.
        readonly List<VelocityTimePair> throwVelocities = new List<VelocityTimePair>();
        readonly List<VelocityTimePair> throwAngularVelocities = new List<VelocityTimePair>();

        // Ventana en la que el registro está desactivado (justo tras crear la conexión de agarre).
        float disableTime;
        float disableSeconds;

        public HandVelocityTracker(HandBase hand) {
            this.hand = hand;
        }

        /// <summary>Vacía las muestras de velocidad registradas.</summary>
        public void ClearThrow() {
            throwVelocities.Clear();
            throwAngularVelocities.Clear();
        }

        /// <summary>Detiene el registro durante <paramref name="seconds"/> (se usa justo tras conectar el agarre).</summary>
        public void Disable(float seconds) {
            disableTime = Time.realtimeSinceStartup;
            disableSeconds = seconds;
            ClearThrow();
        }

        /// <summary>Registra la velocidad actual del objeto sostenido, descartando muestras más viejas que la ventana de expiración.</summary>
        public void UpdateThrowing() {
            // Si estamos en la ventana de desactivación, o no se sostiene nada, o se está agarrando: no registramos.
            if (disableTime + disableSeconds > Time.realtimeSinceStartup || hand.holdingObj == null || hand.IsGrabbing()) {
                if (throwVelocities.Count > 0)
                    ClearThrow();
                return;
            }

            float now = Time.realtimeSinceStartup;
            // Tomamos la velocidad del Rigidbody del objeto (0 si por alguna razón no tiene body).
            Vector3 linear = hand.holdingObj.body == null ? Vector3.zero : hand.holdingObj.body.linearVelocity;
            Vector3 angular = hand.holdingObj.body == null ? Vector3.zero : hand.holdingObj.body.angularVelocity;

            // Añadimos la muestra lineal y eliminamos las que ya hayan expirado (más viejas que el umbral).
            throwVelocities.Add(new VelocityTimePair { time = now, velocity = linear });
            for (int i = throwVelocities.Count - 1; i >= 0; i--)
                if (now - throwVelocities[i].time >= hand.throwVelocityExpireTime)
                    throwVelocities.RemoveAt(i);

            // Lo mismo para la velocidad angular (con su propia ventana de expiración).
            throwAngularVelocities.Add(new VelocityTimePair { time = now, velocity = angular });
            for (int i = throwAngularVelocities.Count - 1; i >= 0; i--)
                if (now - throwAngularVelocities[i].time >= hand.throwAngularVelocityExpireTime)
                    throwAngularVelocities.RemoveAt(i);
        }

        /// <summary>Velocidad lineal de lanzamiento promedio, escalada por la potencia de lanzamiento de la mano.</summary>
        public Vector3 ThrowVelocity() {
            // Sin objeto o en pleno agarre no hay lanzamiento.
            if (hand.IsGrabbing() || hand.holdingObj == null)
                return Vector3.zero;

            // Promediamos las muestras de la ventana para suavizar picos.
            Vector3 average = Vector3.zero;
            if (throwVelocities.Count > 0) {
                foreach (var pair in throwVelocities)
                    average += pair.velocity;
                average /= throwVelocities.Count;
            }

            // Aplicamos el amplificador de lanzamiento de la mano (el del grabbable se aplica aparte al soltar).
            Vector3 vel = average * hand.throwPower;
            return vel.magnitude > minThrowVelocity ? vel : Vector3.zero;
        }

        /// <summary>Velocidad angular de lanzamiento promedio, escalada por la potencia de lanzamiento de la mano.</summary>
        public Vector3 ThrowAngularVelocity() {
            if (hand.IsGrabbing() || hand.holdingObj == null)
                return Vector3.zero;

            Vector3 average = Vector3.zero;
            if (throwAngularVelocities.Count > 0) {
                foreach (var pair in throwAngularVelocities)
                    average += pair.velocity;
                average /= throwAngularVelocities.Count;
            }

            // La componente angular se escala de forma más suave (raíz cuadrada) para que no gire en exceso.
            average *= Mathf.Sqrt(Mathf.Max(0f, hand.throwPower)) / 2f;
            return average.magnitude > minThrowVelocity ? average : Vector3.zero;
        }
    }
}
