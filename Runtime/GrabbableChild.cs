using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Enlace ligero que se coloca en un collider hijo de un grabbable con varios colliders, para que
    /// la mano pueda resolver el <see cref="Grabbable"/> dueño desde cualquiera de sus colliders. Lo
    /// añade automáticamente <see cref="Grabbable"/> cuando <c>makeChildrenGrabbable</c> está activado.
    /// </summary>
    public class GrabbableChild : MonoBehaviour {
        [Label("Grabbable padre", "El grabbable al que pertenece este collider hijo.")]
        public Grabbable grabParent;

        /// <summary>Devuelve el grabbable dueño.</summary>
        public Grabbable GetGrabbable() => grabParent;
    }
}
