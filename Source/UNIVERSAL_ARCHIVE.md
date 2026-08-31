# Релизные архивы DVSeasons 0.2.0

Для публикации создаются два разных ZIP:

- `DVSeasons-0.2.0-Nexus.zip` — только устанавливаемый runtime без исходников и инструментов;
- `DVSeasons-0.2.0-GitHub.zip` — тот же runtime и публикуемый исходный код в `DVSeasons/Source`.

GitHub-архив содержит C#-проекты, тесты, документацию, инструменты разработки, визуальные эталоны и исходный Unity-проект с текстурами. Из обоих архивов исключены кэши и результаты сборки, PDB и вложенные архивы. В GitHub-архиве также нет второй копии готового AssetBundle: единственный bundle и необходимые runtime-PNG лежат в корне мода и одновременно используются исходным проектом при сборке из распакованного архива.

Сборка исходников:

```powershell
dotnet build .\DVSeasons.Game\DVSeasons.Game.csproj -c Release -p:DVInstallDir="D:\Games\Derail Valley"
```

Тесты:

```powershell
dotnet test .\DVSeasons.Tests\DVSeasons.Tests.csproj -c Release
```

Создание обоих релизных архивов:

```powershell
.\Tools\package_combined.ps1 -Version 0.2.0
```
