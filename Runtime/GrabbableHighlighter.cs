using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Encuentra el grabbable al que apunta la mano y activa/desactiva su resaltado. Funciona con un
    /// temporizador (no cada frame) por rendimiento: hace un overlap de esfera alrededor de la palma,
    /// lanza un pequeño cono de rayos para elegir el mejor candidato (favoreciendo la dirección
    /// forward/right de la palma y el peso de prioridad del grabbable) y expone ese candidato para que
    /// <see cref="Hand.Grab()"/> pueda usarlo.
    /// </summary>
    [RequireComponent(typeof(Hand))]
    public class GrabbableHighlighter : MonoBehaviour {

        Hand _hand;
        public Hand hand => _hand != null ? _hand : (_hand = GetComponent<Hand>());

        [Label("Dirección forward/right", "0 = favorece el forward de la palma, 1 = el right (hacia las yemas). 0.5-0.75 recomendado.")]
        public float palmForwardRightDirection = 0.65f;
        [Label("Consulta de resaltado", "Si la comprobación de resaltado tiene en cuenta los triggers o solo colliders sólidos.")]
        public QueryTriggerInteraction highlightQuery = QueryTriggerInteraction.Collide;

        /// <summary>Se dispara cuando un nuevo grabbable pasa a ser el objetivo resaltado.</summary>
        public event HandGrabEvent OnHighlight;
        /// <summary>Se dispara cuando se limpia el objetivo resaltado actual.</summary>
        public event HandGrabEvent OnStopHighlight;

        public Grabbable currentHighlightTarget { get; protected set; }

        RaycastHit _highlightHit;
        // Buffers reutilizables para los overlaps (evitan reservar memoria en cada comprobación).
        readonly Collider[] overlapBuffer = new Collider[128];
        readonly Collider[] coneBuffer = new Collider[128];
        readonly List<Grabbable> foundGrabbables = new List<Grabbable>();
        readonly List<RaycastHit> closestHits = new List<RaycastHit>();
        readonly List<Grabbable> closestGrabs = new List<Grabbable>();

        Coroutine highlightRoutine;

        protected virtual void OnEnable() {
            // Lanzamos la comprobación periódica (~30 veces por segundo).
            highlightRoutine = StartCoroutine(HighlightUpdate(1 / 30f));
        }

        protected virtual void OnDisable() {
            if (highlightRoutine != null)
                StopCoroutine(highlightRoutine);
            // Al desactivar, quitamos el resaltado que hubiera para no dejarlo "encendido".
            if (currentHighlightTarget != null) {
                OnStopHighlight?.Invoke(hand, currentHighlightTarget);
                currentHighlightTarget.Unhighlight(hand);
                currentHighlightTarget = null;
            }
        }

        protected virtual void Update() {
            // Si estamos sosteniendo o agarrando, no hay objetivo resaltado.
            if (hand.holdingObj != null || hand.IsGrabbing())
                currentHighlightTarget = null;
        }

        IEnumerator HighlightUpdate(float timestep) {
            yield return new WaitForEndOfFrame();
            // Desfasamos la mano izquierda medio paso para repartir el coste de las dos manos en el tiempo.
            if (hand.left)
                yield return new WaitForSecondsRealtime(timestep / 2f);
            while (true) {
                if (hand.usingHighlight)
                    UpdateHighlight();
                yield return new WaitForSecondsRealtime(timestep);
            }
        }

        /// <summary>Recalcula el objetivo a resaltar y activa/desactiva el resaltado y sus eventos.</summary>
        public virtual void UpdateHighlight(bool overrideIgnoreHighlight = false, bool ignoreHighlightEvents = false) {
            // No hacemos nada si el resaltado está desactivado o si ya sostenemos/agarramos (salvo que se fuerce).
            if (!overrideIgnoreHighlight && (!hand.usingHighlight || hand.holdingObj != null || hand.IsGrabbing()))
                return;
            if (hand.highlightLayers == 0)
                return;

            int grabbingLayer = LayerMask.NameToLayer(Hand.grabbingLayerName);
            // Capas a comprobar = las de resaltado menos las explícitamente ignoradas.
            int overlapMask = hand.highlightLayers & ~hand.ignoreGrabCheckLayers.value;
            // Esfera de overlap centrada un poco por delante de la palma, con radio = alcance.
            int count = Physics.OverlapSphereNonAlloc(hand.palmTransform.position + hand.palmTransform.forward * hand.reachDistance / 3f, hand.reachDistance, overlapBuffer, overlapMask, highlightQuery);

            // Recogemos los grabbables encontrados y los pasamos temporalmente a la capa "Grabbing"
            // (así el raycast del cono los detecta de forma fiable).
            foundGrabbables.Clear();
            for (int i = 0; i < count; i++) {
                if (overlapBuffer[i].gameObject.HasGrabbable(out var grab)) {
                    grab.SetLayerRecursive(grabbingLayer);
                    foundGrabbables.Add(grab);
                }
            }

            if (foundGrabbables.Count > 0) {
                // Lanzamos el cono de rayos para elegir el mejor candidato (todo menos manos y capas ignoradas).
                int rayMask = ~(HandBase.GetHandsLayerMask() | hand.ignoreGrabCheckLayers.value);
                bool hit = HandClosestHit(out _highlightHit, out var newTarget, rayMask);

                // Si hay un candidato válido y agarrable...
                if (hit && newTarget != null && newTarget.CanGrab(hand)) {
                    // ...y es distinto del actual, cambiamos el resaltado (apagamos el viejo, encendemos el nuevo).
                    if (newTarget != currentHighlightTarget) {
                        if (currentHighlightTarget != null) {
                            if (!ignoreHighlightEvents) OnStopHighlight?.Invoke(hand, currentHighlightTarget);
                            currentHighlightTarget.Unhighlight(hand, null, ignoreHighlightEvents);
                        }
                        currentHighlightTarget = newTarget;
                        if (!ignoreHighlightEvents) OnHighlight?.Invoke(hand, currentHighlightTarget);
                        currentHighlightTarget.Highlight(hand, null, ignoreHighlightEvents);
                    }
                }
                // Si no hay candidato pero teníamos uno, lo apagamos.
                else if (currentHighlightTarget != null) {
                    if (!ignoreHighlightEvents) OnStopHighlight?.Invoke(hand, currentHighlightTarget);
                    currentHighlightTarget.Unhighlight(hand, null, ignoreHighlightEvents);
                    currentHighlightTarget = null;
                }

                // Devolvemos los grabbables a sus capas originales (deshacemos el cambio temporal a "Grabbing").
                for (int i = 0; i < foundGrabbables.Count; i++)
                    foundGrabbables[i].ResetOriginalLayers();
            }
            // No hay nada en alcance: apagamos el resaltado si lo había.
            else if (currentHighlightTarget != null) {
                if (!ignoreHighlightEvents) OnStopHighlight?.Invoke(hand, currentHighlightTarget);
                currentHighlightTarget.Unhighlight(hand, null, ignoreHighlightEvents);
                currentHighlightTarget = null;
            }
        }

        /// <summary>Limpia el resaltado actual sin hacer una nueva comprobación.</summary>
        public void ClearHighlights() {
            if (currentHighlightTarget != null) {
                OnStopHighlight?.Invoke(hand, currentHighlightTarget);
                currentHighlightTarget.Unhighlight(hand);
                currentHighlightTarget = null;
            }
        }

        /// <summary>Devuelve el último hit de resaltado, ajustado al punto de agarre actual.</summary>
        public RaycastHit GetHighlightHit() {
            _highlightHit.point = hand.handGrabPoint.position;
            _highlightHit.normal = hand.handGrabPoint.up;
            return _highlightHit;
        }

        /// <summary>Lanza un cono de rayos hacia los colliders cercanos y devuelve el mejor grabbable candidato.</summary>
        public virtual bool HandClosestHit(out RaycastHit closestHit, out Grabbable grabbable, int layerMask) {
            closestHit = default;
            grabbable = null;

            Vector3 palmForward = hand.palmTransform.forward;
            Vector3 palmRight = hand.palmTransform.right;
            Vector3 palmPosition = hand.palmTransform.position;

            closestHits.Clear();
            closestGrabs.Clear();

            // Esfera un poco mayor que el alcance, centrada por delante de la palma.
            float checkRadius = hand.reachDistance * 1.35f;
            int overlapCount = Physics.OverlapSphereNonAlloc(palmPosition + palmForward * (checkRadius * 0.5f), checkRadius, coneBuffer, layerMask, highlightQuery);

            // Para cada collider cercano lanzamos un rayo hacia él y, si toca un grabbable agarrable, lo guardamos.
            for (int i = 0; i < overlapCount; i++) {
                var col = coneBuffer[i];
                Ray ray = new Ray { origin = palmPosition };

                // Dirección del rayo: hacia el punto más cercano del collider (salvo mesh cóncavo, donde usamos el forward).
                if (!(col is MeshCollider mesh) || mesh.convex) {
                    Vector3 closestPoint = col.ClosestPoint(palmPosition);
                    ray.direction = closestPoint - palmPosition;
                }
                else {
                    ray.direction = palmForward;
                }
                // Empujamos un poquito el origen hacia el centro del collider (evita empezar dentro de él).
                ray.origin = Vector3.MoveTowards(ray.origin, col.bounds.center, 0.001f);

                // Solo consideramos rayos dentro de un cono de ±120° respecto al forward de la palma.
                if (ray.direction != Vector3.zero
                    && Vector3.Angle(ray.direction, palmForward) < 120f
                    && Physics.Raycast(ray, out var hit, checkRadius * 2f, layerMask, highlightQuery)) {
                    if (hit.collider.gameObject.HasGrabbable(out var grab) && hand.CanGrab(grab)) {
                        closestGrabs.Add(grab);
                        closestHits.Add(hit);
                    }
                }
            }

            // Si no hemos tocado ningún grabbable, no hay candidato.
            if (closestHits.Count == 0)
                return false;

            // Empezamos suponiendo que el mejor es el primero y lo comparamos con el resto.
            closestHit = closestHits[0];
            grabbable = closestGrabs[0];
            // Dirección "ideal" de agarre: mezcla entre forward y right de la palma según el ajuste.
            var targetDirection = Vector3.Lerp(palmForward, palmRight, palmForwardRightDirection);

            for (int i = 0; i < closestHits.Count; i++) {
                // Puntuación: distancia (penalizada por el peso de prioridad) menos un bonus por alinearse con la dirección ideal.
                float newDistance = closestHits[i].distance / Mathf.Max(0.0001f, closestGrabs[i].grabPriorityWeight);
                float newDot = Vector3.Dot(targetDirection, closestHits[i].point - palmPosition) / 2f * hand.reachDistance;
                float curDistance = closestHit.distance / Mathf.Max(0.0001f, grabbable.grabPriorityWeight);
                float curDot = Vector3.Dot(targetDirection, closestHit.point - palmPosition) / 2f * hand.reachDistance;

                // Menor "distancia - bonus" gana.
                if (newDistance - newDot < curDistance - curDot) {
                    closestHit = closestHits[i];
                    grabbable = closestGrabs[i];
                }
            }

            // Si no sostenemos nada, dejamos el punto de agarre colgado del collider tocado, en el punto del hit.
            if (hand.holdingObj == null && !hand.IsGrabbing()) {
                if (hand.handGrabPoint.parent != closestHit.collider.transform)
                    hand.handGrabPoint.parent = closestHit.collider.transform;
                hand.handGrabPoint.position = closestHit.point;
                hand.handGrabPoint.up = closestHit.normal;
            }

            return true;
        }
    }
}
