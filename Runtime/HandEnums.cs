using UnityEngine;
using UnityEngine.Events;

namespace ProceduralHands {

    /// <summary>Los cinco dedos de una mano. <c>none</c> (-1) marca un dedo sin configurar.</summary>
    public enum FingerEnum {
        none = -1,
        index,   // índice
        middle,  // medio
        ring,    // anular
        pinky,   // meñique
        thumb    // pulgar
    }

    /// <summary>Las cuatro articulaciones que se registran por dedo, del nudillo a la punta.</summary>
    public enum FingerJointEnum {
        knuckle, // nudillo
        middle,  // media
        distal,  // distal
        tip      // punta
    }

    /// <summary>
    /// Huecos de pose guardada por dedo. NO reordenar los valores existentes; solo añadir nuevos al
    /// final, porque los índices se serializan dentro de <see cref="Finger.poseData"/>.
    /// </summary>
    public enum FingerPoseEnum {
        Open = 0,        // mano abierta
        Closed = 1,      // mano cerrada (puño)
        PinchOpen = 2,   // pinza abierta
        PinchClosed = 3, // pinza cerrada
        TotalPoses       // número total de poses (no es una pose, sirve para dimensionar arrays)
    }

    /// <summary>Cómo realiza la mano un agarre una vez elegido el objetivo.</summary>
    public enum GrabType {
        /// <summary>La mano viaja hasta el grabbable, conecta y luego vuelve al objetivo de seguimiento.</summary>
        HandToGrabbable,
        /// <summary>El grabbable viaja hasta la mano y luego conecta.</summary>
        GrabbableToHand,
        /// <summary>La conexión de agarre se crea al instante, sin animación de viaje.</summary>
        InstantGrab
    }

    /// <summary>Override por grabbable del <see cref="GrabType"/> por defecto de la mano.</summary>
    public enum HandGrabType {
        Default,         // usa el tipo por defecto de la mano
        HandToGrabbable,
        GrabbableToHand
    }

    /// <summary>Si un grabbable se sostiene con una pose de agarre completa o de pinza.</summary>
    public enum HandGrabPoseType {
        Grab,  // agarre completo (toda la mano)
        Pinch, // pinza (pulgar + índice)
        Climb  // escalada: la mano se posa y se ancla al asidero sin física (XRI mueve al jugador)
    }

    /// <summary>Qué mano(s) pueden agarrar un grabbable.</summary>
    public enum HandType {
        both,  // ambas
        right, // solo derecha
        left,  // solo izquierda
        none   // ninguna
    }

    /// <summary>Evento que aporta la mano y el grabbable implicados (el grabbable puede ser null).</summary>
    public delegate void HandGrabEvent(Hand hand, Grabbable grabbable);

    /// <summary>Evento que aporta la mano y un GameObject cualquiera (colisión/trigger).</summary>
    public delegate void HandGameObjectEvent(Hand hand, GameObject other);

    /// <summary>UnityEvent serializable en el inspector que lleva una mano y un grabbable.</summary>
    [System.Serializable]
    public class UnityHandGrabEvent : UnityEvent<Hand, Grabbable> { }

    /// <summary>UnityEvent serializable en el inspector que lleva una mano.</summary>
    [System.Serializable]
    public class UnityHandEvent : UnityEvent<Hand> { }

    /// <summary>Una muestra de velocidad etiquetada con el instante (realtime) en que se capturó; se usa para el lanzamiento.</summary>
    [System.Serializable]
    public struct VelocityTimePair {
        public float time;       // momento (Time.realtimeSinceStartup) de la muestra
        public Vector3 velocity; // velocidad capturada en ese momento
    }
}
