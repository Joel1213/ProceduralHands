using UnityEditor;
using UnityEngine;

namespace ProceduralHands.EditorTools {
    /// <summary>
    /// Dibuja en el Inspector los campos marcados con <see cref="LabelAttribute"/>, usando la
    /// etiqueta y el tooltip en español del atributo. Soporta las variantes con rango (slider) y
    /// con mínimo, y para cualquier otro tipo de campo delega en el dibujo por defecto.
    /// </summary>
    [CustomPropertyDrawer(typeof(LabelAttribute))]
    public class LabelAttributeDrawer : PropertyDrawer {

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            var labelAttribute = (LabelAttribute)attribute;
            // Reemplazamos el texto y el tooltip que mostraría Unity por los nuestros en español.
            var content = new GUIContent(labelAttribute.Label, labelAttribute.Tooltip);

            // Caso slider: rango [Min, Max] sobre float o int.
            if (labelAttribute.HasMin && labelAttribute.HasMax) {
                if (property.propertyType == SerializedPropertyType.Float) {
                    EditorGUI.Slider(position, property, labelAttribute.Min, labelAttribute.Max, content);
                    return;
                }
                if (property.propertyType == SerializedPropertyType.Integer) {
                    EditorGUI.IntSlider(position, property, (int)labelAttribute.Min, (int)labelAttribute.Max, content);
                    return;
                }
            }

            // Resto de casos: dibujo normal del campo (incluye hijos para arrays/listas/estructuras).
            EditorGUI.PropertyField(position, property, content, true);

            // Caso solo mínimo: acotamos el valor tras editarlo.
            if (labelAttribute.HasMin && !labelAttribute.HasMax) {
                if (property.propertyType == SerializedPropertyType.Float && property.floatValue < labelAttribute.Min)
                    property.floatValue = labelAttribute.Min;
                else if (property.propertyType == SerializedPropertyType.Integer && property.intValue < (int)labelAttribute.Min)
                    property.intValue = (int)labelAttribute.Min;
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) {
            // Respetamos la altura real del campo (importante para arrays/listas desplegables).
            return EditorGUI.GetPropertyHeight(property, label, true);
        }
    }
}
