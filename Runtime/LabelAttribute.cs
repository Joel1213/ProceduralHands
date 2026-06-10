using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Atributo para mostrar un campo en el Inspector con una etiqueta y un tooltip personalizados
    /// (en español), manteniendo el nombre de la variable en inglés en el código.
    ///
    /// Sustituye a <c>[Tooltip]</c>, <c>[Range]</c> y <c>[Min]</c> en los campos serializados, ya que
    /// Unity solo permite un PropertyDrawer por campo. Para ello incluye variantes con rango (slider)
    /// y con mínimo. Los <c>[Header]</c> y <c>[Space]</c> sí pueden seguir usándose junto a este atributo.
    ///
    /// Uso:
    /// <code>
    /// [Label("Distancia de alcance", "Distancia máxima a la que la mano puede agarrar.", 0f)]
    /// public float reachDistance = 0.2f;
    /// </code>
    /// </summary>
    public class LabelAttribute : PropertyAttribute {
        /// <summary>Texto que se mostrará como nombre del campo en el Inspector (en español).</summary>
        public readonly string Label;
        /// <summary>Texto de ayuda que aparece al pasar el ratón por encima (en español).</summary>
        public readonly string Tooltip;
        /// <summary>Indica si el campo tiene un valor mínimo definido.</summary>
        public readonly bool HasMin;
        /// <summary>Indica si el campo tiene un valor máximo definido (junto con el mínimo, se dibuja como slider).</summary>
        public readonly bool HasMax;
        /// <summary>Valor mínimo permitido (si <see cref="HasMin"/> es true).</summary>
        public readonly float Min;
        /// <summary>Valor máximo permitido (si <see cref="HasMax"/> es true).</summary>
        public readonly float Max;

        /// <summary>Campo normal: solo etiqueta y tooltip.</summary>
        public LabelAttribute(string label, string tooltip = "") {
            Label = label;
            Tooltip = tooltip;
        }

        /// <summary>Campo numérico acotado por abajo: muestra el campo y fuerza un valor mínimo.</summary>
        public LabelAttribute(string label, string tooltip, float min) {
            Label = label;
            Tooltip = tooltip;
            Min = min;
            HasMin = true;
        }

        /// <summary>Campo numérico con rango: se dibuja como un slider entre <paramref name="min"/> y <paramref name="max"/>.</summary>
        public LabelAttribute(string label, string tooltip, float min, float max) {
            Label = label;
            Tooltip = tooltip;
            Min = min;
            Max = max;
            HasMin = true;
            HasMax = true;
        }
    }
}
