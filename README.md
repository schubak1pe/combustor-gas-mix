# Combustor Gas Mix

**Descarga:** [CombustorGasMix.zip](https://github.com/schubak1pe/combustor-gas-mix/releases/latest)

Mod de MelonLoader para Combustor que permite que un inyector ya colocado
alimente varios gases a la vez, con porcentajes ajustables desde el panel del
inyector.

## Botones de la mezcla

- **Aplicar a todos** — copia la composición actual a todos los inyectores y
  cambia también su gas principal, así que todos acaban con la misma mezcla.
- **Quitar mezcla** — vuelve al comportamiento de un solo gas, solo en el
  inyector seleccionado.

## Como funciona

El juego representa cada inyector como un `GasSim.GasGenerator` (`Generator` en
los interop assemblies) con un solo `species` y un `rate`. El mod no crea un
inyector nuevo ni cambia la UI del juego: durante `GasGrid.RunGenerators` divide
el caudal del inyector entre copias temporales del generador, una por gas, y las
quita en el mismo tick. El inyector original sigue siendo el dueno de la pieza,
del guardado, del undo y de los contadores del juego.

La mezcla se guarda en un sidecar `.gasmix` junto al `.engine`, igual que hace
el mod de referencia con `.pngcolors`.

## Estructura

- `GasMixMod.cs` — MelonMod, parches Harmony, inyeccion, persistencia y UI.
- `SelfTest.cs` — prueba automatica opcional, excluida de la build normal.
- `Properties/AssemblyInfo.cs` — MelonInfo.
- `CombustorGasMix.csproj` — compila contra el juego instalado.

## Compilar

```powershell
dotnet build -c Release
```

Si el juego no esta en la ruta por defecto:

```powershell
dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\EngineSimulator"
```

La DLL queda en `bin\Release\net6.0\CombustorGasMix.dll` y se copia a
`Mods\CombustorGasMix.dll`.

### Prueba automatica

```powershell
dotnet build -c Release -p:GasMixSelfTest=true
```

La build de prueba arranca Sandbox, coloca un inyector, inyecta Fuel + Oxidizer
al 50 %, comprueba el gas, guarda y vuelve a cargar. Los resultados salen en
`MelonLoader/Latest.log` con el prefijo `[Gas Mix] SELFTEST`. No incluir esa DLL
en el ZIP final.

## Notas

- Usa los estilos IMGUI del propio juego (`_button`, `_label`, `_dim`), asi que
  la tipografia es la misma.
- `GasSimInput.Update` se bloquea solo mientras el selector de gas esta abierto
  o el raton esta encima de la seccion de mezcla, para que el clic no caiga en
  la paleta de gases de debajo.
- La mezcla no viaja en los codigos de compartir; solo en el `.gasmix`.
- Los cambios de mezcla no entran en el undo del juego: se consolidan al guardar
  el motor. El resto de la pieza (colocarla, borrarla, cambiar caudal) sigue
  usando el undo normal.
