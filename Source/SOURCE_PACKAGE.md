# Исходный код в GitHub-архиве

Каталог `Source` архива `DVSeasons-0.2.0-GitHub.zip` содержит исходный код DVSeasons 0.2.0, тесты, документацию и Unity-проект сезонных ресурсов. Готовые runtime-ресурсы намеренно не дублируются: единственный AssetBundle и PNG, необходимые C#-сборке, находятся в родительском каталоге `DVSeasons/AssetBundles` и `DVSeasons/Textures`.

Из GitHub-архива исключены только генерируемые `bin`, `obj`, Unity-кэши, PDB, результаты сборки, вложенные архивы и дублирующие runtime-ресурсы. PowerShell/Python-инструменты и визуальные эталоны находятся в `Source`, но не входят в отдельный Nexus-пакет.

Прямая сборка после распаковки общего архива:

```powershell
dotnet build .\Source\DVSeasons.Game\DVSeasons.Game.csproj -c Release -p:DVInstallDir="D:\Games\Derail Valley"
```
