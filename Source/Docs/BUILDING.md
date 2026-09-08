# Сборка DVSeasons

## Требования

- Windows и PowerShell 5.1+;
- 64-битный .NET SDK 8.0.421 или совместимый более новый SDK;
- установленная Derail Valley build 99;
- Unity Mod Manager в каталоге игры;
- для сборки Multiplayer-адаптера — установленный мод Multiplayer с `MultiplayerAPI.dll`.

Основная сборка не записывает файлы в игру. Игра используется только как источник compile-time DLL.

## Обычная сборка

```powershell
.\Tools\build.ps1
```

Скрипт ищет игру в Steam-каталогах на доступных файловых дисках. При нестандартной установке передайте путь:

```powershell
.\Tools\build.ps1 -DVInstallDir "D:\Games\Derail Valley"
```

Перед каждой сборкой `build.ps1` запускает `Tools/check_mod_updates.ps1`: установленные моды из `LoadAfter` сверяются с их официальными UMM `Repository`-лентами. Устаревшая версия выводится предупреждением до компиляции; отсутствие необязательного мода или временная недоступность сети сборку не блокируют. Для строгой отдельной проверки, которая завершится ошибкой при найденном обновлении:

```powershell
.\Tools\check_mod_updates.ps1 -DVInstallDir "D:\Games\Derail Valley" -FailOnOutdated
```

Тесты можно запустить отдельно:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" test .\DVSeasons.Tests\DVSeasons.Tests.csproj -c Release
```

Прямая сборка runtime-проекта:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\DVSeasons.Game\DVSeasons.Game.csproj -c Release -p:DVInstallDir="D:\Games\Derail Valley"
```

Готовая устанавливаемая папка создаётся в `artifacts/build/DVSeasons`.

Установочный ZIP для Nexus Mods и единый ZIP с исходниками для GitHub создаются так:

```powershell
.\Tools\package_combined.ps1 -Version 0.2.12
```

`DVSeasons-0.2.12-Nexus.zip` содержит только устанавливаемый runtime. `DVSeasons-0.2.12-GitHub.zip` содержит тот же runtime и публикуемые исходники. Размер самого локального архива может превышать 100 МБ: перед загрузкой в репозиторий его содержимое распаковывается. Скрипт останавливает упаковку, если любой runtime/source-файл или любая запись ZIP в распакованном виде достигает 100 000 000 байт.

Из runtime-части обоих пакетов исключены восемь побайтово одинаковых PNG с другими именами. Загрузчик сопоставляет их с каноническими файлами. В Unity-исходниках все именованные импорты сохраняются для воспроизводимой пересборки AssetBundle.

Внутри GitHub ZIP есть каталог `DVSeasons` с тем же runtime и каталогом `Source`, в котором находятся C#-проекты, тесты, документация, инструменты, визуальные эталоны и полный Unity-проект. PDB, кэши, результаты сборки, вложенные архивы и дублирующий AssetBundle в релиз не включаются.

## AssetBundle

Обычная C#-сборка использует проверенный bundle из `Resources/Runtime/AssetBundles`. Для его пересборки требуется Unity 2019.4.40f1:

```powershell
.\Tools\build_assetbundle.ps1 -UnityEditor "C:\Program Files\Unity\Hub\Editor\2019.4.40f1\Editor\Unity.exe"
```

Unity проверяет все 171 исходных PNG и собирает deterministic LZ4 bundle из 75 сезонных `Texture2D` растительности, пяти шейдеров и трёх 16-слойных `Texture2DArray` земли. Отдельные 48 PNG земли уже входят в эти массивы, а 48 текстур путей, поверхностей и льда загружаются из 40 канонических runtime-PNG. Builder исключает их копии из bundle только после проверки наличия и побайтового совпадения. Перед LZMA-перепаковкой проверяются точный состав bundle, глубина массивов, ошибки шейдеров и GPU-обработка льда. Скрипт ожидает завершения процесса редактора; после проверки побайтового соответствия распакованного содержимого runtime-копия заменяется. Для перепаковки нужен Python с пакетами из `Tools/requirements.txt`.

После C#-сборки дополнительная проверка реального текстурного композитора запускается Unity `-executeMethod DVSeasons.AssetBundleBuild.SurfaceSnowVerification.Verify`. Она проверяет порционную обработку, четыре стадии, неизменность alpha и побайтовое восстановление при таянии; PNG-превью сохраняется в `artifacts/verification/0.2.12`.
