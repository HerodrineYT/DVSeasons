# Релизные пакеты DVSeasons

Для публикации создаются два пакета:

- `DVSeasons-<version>-Nexus.zip` — готовый к установке runtime без исходников и инструментов;
- `DVSeasons-<version>-GitHub.zip` — тот же runtime и публикуемые исходники в `DVSeasons/Source`.

GitHub ZIP служит единым локальным пакетом. Перед публикацией его распаковывают и загружают содержимое в репозиторий. Общий архив может быть больше 100 МБ, но каждый входной файл и каждая запись ZIP в распакованном виде обязаны быть меньше 100 000 000 байт; это проверяет упаковщик.

В GitHub-пакет входят C#-проекты, тесты, документация, инструменты, визуальные эталоны и полный Unity-проект с текстурами. Из релиза исключены кэши, результаты сборки, PDB и вложенные архивы. Готовый AssetBundle хранится в пакете один раз.

GitHub-пакет распаковывается любым ZIP-совместимым архиватором.

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
.\Tools\package_combined.ps1 -Version <version>
```
