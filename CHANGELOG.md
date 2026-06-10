# Changelog

Todos los cambios notables de este package se documentan aquí.
El formato sigue [Keep a Changelog](https://keepachangelog.com/es/1.0.0/).

## [0.1.0] - 2026-06-04

### Added
- Versión inicial del core a una mano.
- `Hand` / `HandBase`: agarre, highlight, posing y movimiento físico con Rigidbody.
- `Finger`: datos de pose abierta/cerrada y flexión procedural (`BendFingerUntilHit`).
- `HandPoseData` / `FingerPoseData`: structs de pose con guardado e interpolación.
- `HandFollower`: seguimiento físico del controller (velocity + torque).
- `HandAnimator`: posing procedural (sway, grip, poses de grab).
- `GrabbableHighlighter`: resaltado por raycast desde la palma.
- `Grabbable` + `PlacePoint`: objetos agarrables y puntos de colocación.
- Poses: `GrabbablePose`, `GrabbablePoseAdvanced`, `GrabbablePoseCombiner`,
  `GrabbablePoseAnimation`, `HandPoseScriptable`.
- Input: `HandControllerLink` + `XRHandControllerLink` (Input System / XR Toolkit).
- Editor: inspectores custom, ventana flotante "Hand Pose Tool" y
  Setup Wizard (`Tools > Procedural Hands > Setup Wizard`).
