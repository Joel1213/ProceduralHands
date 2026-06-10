using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
// Tanto UnityEngine.InputSystem como UnityEngine.XR definen "InputDevice"; usamos el de XR para la háptica.
using InputDevice = UnityEngine.XR.InputDevice;

namespace ProceduralHands {
    /// <summary>
    /// Controla una <see cref="Hand"/> a partir de acciones del Input System (compatible con el set de
    /// acciones del XR Interaction Toolkit). Pulsar/soltar el botón de agarre llama a
    /// <see cref="Hand.Grab()"/> / <see cref="Hand.Release()"/>, el botón de squeeze llama a
    /// squeeze/unsqueeze, y los ejes analógicos de grip/squeeze alimentan
    /// <see cref="Hand.SetGrip(float, float)"/> para el posing de los dedos.
    /// </summary>
    public class XRHandControllerLink : HandControllerLink {

        [Header("Ejes analógicos (0..1)")]
        [Label("Eje de grip", "Valor analógico 0..1 del grip; es lo que cierra los dedos en el aire.")]
        public InputActionProperty gripAxis;
        [Label("Eje de gatillo", "Valor analógico 0..1 del gatillo (squeeze).")]
        public InputActionProperty squeezeAxis;

        [Header("Botones")]
        [Label("Acción de agarrar", "Botón: al pulsar agarra, al soltar suelta.")]
        public InputActionProperty grabAction;
        [Label("Acción de squeeze", "Botón: al pulsar lanza 'squeeze', al soltar 'unsqueeze'.")]
        public InputActionProperty squeezeAction;

        // Estado para no llamar dos veces a grab/squeeze mientras se mantiene pulsado.
        bool grabbing;
        bool squeezing;
        // Nodo XR (mano izquierda/derecha) para dirigir la háptica al dispositivo correcto.
        XRNode node;
        // Lista reutilizable de dispositivos para la háptica (evita reservar memoria).
        readonly List<InputDevice> devices = new List<InputDevice>();

        protected virtual void Start() {
            // Registramos este enlace como el de la izquierda o el de la derecha (lo usa la háptica estática).
            if (hand.left)
                handLeft = this;
            else
                handRight = this;
            // Elegimos el nodo XR según el lado de la mano.
            node = hand.left ? XRNode.LeftHand : XRNode.RightHand;
        }

        protected virtual void OnEnable() {
            // Habilitamos los ejes analógicos (su valor se lee cada frame en Update).
            EnableAxis(gripAxis);
            EnableAxis(squeezeAxis);

            // Suscribimos los botones: 'performed' = pulsado, 'canceled' = soltado.
            if (grabAction.action != null) {
                grabAction.action.Enable();
                grabAction.action.performed += OnGrab;
                grabAction.action.canceled += OnRelease;
            }
            if (squeezeAction.action != null) {
                squeezeAction.action.Enable();
                squeezeAction.action.performed += OnSqueeze;
                squeezeAction.action.canceled += OnStopSqueeze;
            }
        }

        protected virtual void OnDisable() {
            // Nos desuscribimos para no dejar callbacks colgando al desactivar el componente.
            if (grabAction.action != null) {
                grabAction.action.performed -= OnGrab;
                grabAction.action.canceled -= OnRelease;
            }
            if (squeezeAction.action != null) {
                squeezeAction.action.performed -= OnSqueeze;
                squeezeAction.action.canceled -= OnStopSqueeze;
            }
        }

        protected virtual void Update() {
            if (hand == null)
                return;
            // Cada frame pasamos a la mano los valores analógicos de grip y squeeze (para el cierre de dedos).
            hand.SetGrip(ReadAxis(gripAxis), ReadAxis(squeezeAxis));
        }

        // Habilita una acción de eje si está asignada.
        static void EnableAxis(InputActionProperty axis) {
            if (axis.action != null)
                axis.action.Enable();
        }

        // Lee el valor float de un eje (0 si la acción no está asignada).
        static float ReadAxis(InputActionProperty axis) {
            return axis.action != null ? axis.action.ReadValue<float>() : 0f;
        }

        // Botón de agarre pulsado: agarramos una sola vez (el flag evita repeticiones).
        void OnGrab(InputAction.CallbackContext _) {
            if (!grabbing) {
                hand.Grab();
                grabbing = true;
            }
        }

        // Botón de agarre soltado: soltamos.
        void OnRelease(InputAction.CallbackContext _) {
            if (grabbing) {
                hand.Release();
                grabbing = false;
            }
        }

        // Botón de squeeze pulsado.
        void OnSqueeze(InputAction.CallbackContext _) {
            if (!squeezing) {
                hand.Squeeze();
                squeezing = true;
            }
        }

        // Botón de squeeze soltado.
        void OnStopSqueeze(InputAction.CallbackContext _) {
            if (squeezing) {
                hand.Unsqueeze();
                squeezing = false;
            }
        }

        /// <summary>Envía un impulso háptico al dispositivo XR de esta mano, si lo soporta.</summary>
        public override void TryHapticImpulse(float duration, float amplitude, float frequency = 10f) {
            // Obtenemos los dispositivos del nodo (mano izq/der) y enviamos el impulso a los que soporten háptica.
            InputDevices.GetDevicesAtXRNode(node, devices);
            foreach (var device in devices)
                if (device.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse)
                    device.SendHapticImpulse(0u, amplitude, duration);
        }
    }
}
