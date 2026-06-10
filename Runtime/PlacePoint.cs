using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ProceduralHands {
    /// <summary>UnityEvent que lleva el place point y el grabbable implicados.</summary>
    [System.Serializable]
    public class PlacePointEvent : UnityEvent<PlacePoint, Grabbable> { }

    /// <summary>
    /// Zona de colocación (snap) para grabbables. Detecta los grabbables permitidos dentro de
    /// <see cref="radius"/> y los asocia, de modo que al soltarlos cerca el objeto encaja en el
    /// transform de este punto.
    /// </summary>
    public class PlacePoint : MonoBehaviour {

        [Label("Radio", "Radio de detección/colocación.")]
        public float radius = 0.1f;
        [Label("Parentar al colocar", "Hace el objeto colocado hijo de este punto.")]
        public bool parentOnPlace = true;
        [Label("Desactivar al colocar", "Desactiva este place point una vez se coloca algo.")]
        public bool disablePlacePointOnPlace = false;

        [Label("Solo permite", "Si no está vacío, solo estos grabbables pueden colocarse aquí.")]
        public List<Grabbable> onlyAllows = new List<Grabbable>();
        [Label("No permite", "Estos grabbables nunca pueden colocarse aquí.")]
        public List<Grabbable> dontAllows = new List<Grabbable>();

        [Label("Al colocar", "Evento al colocar un objeto.")]
        public PlacePointEvent onPlace = new PlacePointEvent();
        [Label("Al quitar", "Evento al quitar el objeto colocado.")]
        public PlacePointEvent onRemove = new PlacePointEvent();
        [Label("Al resaltar", "Evento cuando un grabbable entra en rango y se asocia a este punto.")]
        public PlacePointEvent onHighlight = new PlacePointEvent();
        [Label("Al dejar de resaltar", "Evento cuando el grabbable asociado sale de rango.")]
        public PlacePointEvent onStopHighlight = new PlacePointEvent();

        /// <summary>El grabbable colocado aquí ahora mismo, si lo hay.</summary>
        public Grabbable placedObject { get; protected set; }

        readonly Collider[] buffer = new Collider[32]; // buffer reutilizable para el overlap
        int detectMask;          // máscara de capas a detectar (Grabbable + Grabbing)
        Grabbable highlighted;   // grabbable actualmente asociado (en rango, sin colocar)

        protected virtual void Awake() {
            // Construimos la máscara de detección con las capas Grabbable y Grabbing (o todo si no existen).
            int mask = 0;
            int grabbable = LayerMask.NameToLayer(Hand.grabbableLayerNameDefault);
            int grabbing = LayerMask.NameToLayer(Hand.grabbingLayerName);
            if (grabbable >= 0) mask |= 1 << grabbable;
            if (grabbing >= 0) mask |= 1 << grabbing;
            detectMask = mask == 0 ? ~0 : mask;
        }

        protected virtual void FixedUpdate() {
            // Si ya hay algo colocado, no buscamos nuevos candidatos.
            if (placedObject != null)
                return;

            // Buscamos el grabbable permitido más cercano y lo asociamos a este punto.
            Grabbable nearest = FindNearest();
            if (nearest != highlighted) {
                // Si cambiamos de candidato, desasociamos el anterior...
                if (highlighted != null && highlighted.placePoint == this) {
                    highlighted.SetPlacePoint(null);
                    onStopHighlight?.Invoke(this, highlighted);
                }
                highlighted = nearest;
                // ...y asociamos el nuevo (así, al soltarlo, sabrá que puede colocarse aquí).
                if (highlighted != null) {
                    highlighted.SetPlacePoint(this);
                    onHighlight?.Invoke(this, highlighted);
                }
            }
        }

        // Devuelve el grabbable permitido más cercano dentro del radio.
        Grabbable FindNearest() {
            int count = Physics.OverlapSphereNonAlloc(transform.position, radius, buffer, detectMask, QueryTriggerInteraction.Ignore);
            Grabbable best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < count; i++) {
                if (buffer[i].gameObject.HasGrabbable(out var grab) && CanPlace(grab)) {
                    float d = Vector3.Distance(grab.rootTransform.position, transform.position);
                    if (d < bestDist) {
                        bestDist = d;
                        best = grab;
                    }
                }
            }
            return best;
        }

        /// <summary>Indica si <paramref name="grab"/> puede colocarse aquí ahora mismo.</summary>
        public bool CanPlace(Grabbable grab) {
            if (grab == null || !enabled)
                return false;
            // No si ya hay otro objeto colocado.
            if (placedObject != null && placedObject != grab)
                return false;
            // Respetamos las listas de permitidos/prohibidos.
            if (onlyAllows.Count > 0 && !onlyAllows.Contains(grab))
                return false;
            return !dontAllows.Contains(grab);
        }

        /// <summary>Encaja <paramref name="grab"/> en este punto.</summary>
        public void Place(Grabbable grab) {
            if (!CanPlace(grab))
                return;

            // Movemos el objeto a la pose del punto y anulamos su velocidad.
            var root = grab.rootTransform;
            root.SetPositionAndRotation(transform.position, transform.rotation);
            if (grab.body != null) {
                grab.body.position = transform.position;
                grab.body.rotation = transform.rotation;
                grab.body.linearVelocity = Vector3.zero;
                grab.body.angularVelocity = Vector3.zero;
            }
            // Opcionalmente lo hacemos hijo del punto para que lo acompañe si el punto se mueve.
            if (parentOnPlace)
                root.parent = transform;

            placedObject = grab;
            grab.SetPlacePoint(this);
            highlighted = null;
            onPlace?.Invoke(this, grab);

            // Si procede, desactivamos el punto para que no acepte nada más.
            if (disablePlacePointOnPlace)
                enabled = false;
        }

        /// <summary>Quita <paramref name="grab"/> de este punto (p. ej. al volver a agarrarlo).</summary>
        public void Remove(Grabbable grab) {
            if (placedObject == grab) {
                placedObject = null;
                // Si lo habíamos parentado, lo devolvemos a su padre original.
                if (parentOnPlace && grab.rootTransform.parent == transform)
                    grab.rootTransform.parent = grab.originalParent;
                onRemove?.Invoke(this, grab);
                // Reactivamos el punto si se había desactivado al colocar.
                if (disablePlacePointOnPlace)
                    enabled = true;
            }
            if (highlighted == grab)
                highlighted = null;
            if (grab != null && grab.placePoint == this)
                grab.SetPlacePoint(null);
        }

        protected virtual void OnDrawGizmosSelected() {
            // Esfera del radio de colocación + línea hacia su "forward".
            Gizmos.color = new Color(0.3f, 1f, 0.5f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, radius);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * radius);
        }
    }
}
