# Procedural Hands

Sistema de **manos procedurales para VR** en Unity: manos físicas basadas en `Rigidbody`
que siguen al controller, **agarre automático** con flexión de dedos procedural, y un
sistema de **poses** (estática, avanzada por eje/radio y animada).

Es una reimplementación **independiente** inspirada en el comportamiento de *Auto Hand*.
No contiene ni copia código del package original; usa únicamente Unity built-in physics y
el Input System / XR Interaction Toolkit.

> Estado: **0.1.0** — core enfocado a una mano. Ver `CHANGELOG.md`.

## Requisitos

- Unity **6.3 LTS** (6000.3+)
- **Input System** (`com.unity.inputsystem`)
- **XR Interaction Toolkit** (`com.unity.xr.interaction.toolkit`) para el rig/controllers
- Universal Render Pipeline (URP) recomendado para los materiales de highlight

## Instalación

Como package local (UPM):

1. Copia la carpeta `ProceduralHands/` dentro de la carpeta `Packages/` de tu proyecto, o
2. En `Package Manager > Add package from disk...` selecciona `ProceduralHands/package.json`.

## Puesta en marcha rápida

Abre **`Tools > Procedural Hands > Setup Wizard`** y sigue los pasos:

1. **Física y Layers** — crea las layers `Hand`, `Grabbable`, `Grabbing` y aplica un
   preset de física recomendado.
2. **Crear mano** — arrastra tu modelo de mano; el wizard añade y configura los
   componentes (`Hand`, `HandFollower`, `HandAnimator`, `GrabbableHighlighter`, `Finger`).
3. **Asignar huesos** — asigna por dedo knuckle/middle/distal/tip (con ayudas en el
   inspector de `Finger`).
4. **Guardar poses** — guarda las poses *Open* y *Closed* de la mano.
5. **Crear grabbable** — convierte un objeto en agarrable (Rigidbody + `Grabbable`).
6. **Input** — añade `XRHandControllerLink` a cada mano y mapea las acciones de XR.

## Componentes principales

| Componente | Responsabilidad |
|---|---|
| `Hand` | Agarre, highlight, posing y movimiento físico. |
| `Finger` | Pose abierta/cerrada y flexión procedural (va en el nudillo). |
| `HandFollower` | Sigue al controller con fuerzas de Rigidbody. |
| `HandAnimator` | Posing procedural de dedos (sway, grip, poses de grab). |
| `GrabbableHighlighter` | Detecta y resalta el grabbable apuntado. |
| `Grabbable` | Objeto agarrable (Rigidbody + collider) vía physics joint. |
| `GrabbablePose` / `…Advanced` / `…Animation` | Poses estática / por eje-radio / animada. |
| `PlacePoint` | Punto de colocación (snap). |
| `XRHandControllerLink` | Conecta el input de XR Toolkit a la mano. |

## Estructura

```
ProceduralHands/
  Runtime/   ← componentes de juego (namespace ProceduralHands)
  Editor/    ← inspectores, Hand Pose Tool y Setup Wizard
```

## Licencia

MIT. Ver `LICENSE.md`.
