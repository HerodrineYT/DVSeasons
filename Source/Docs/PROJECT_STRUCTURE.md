# Структура исходного проекта

DVSeasons оформлен как самостоятельный публикуемый проект. В его корне нет исходников соседних модов, декомпилированных сторонних сборок, Unity-кэша или файлов установленной игры.

## Публикуемая часть

- `DVSeasons.Common` — модель сезона и независимые от Unity контракты. Проект сохраняет runtime-имя сборки `DVSeasons.Core.dll` для обратной совместимости.
- `DVSeasons.Game` — точка входа Unity Mod Manager, визуальные эффекты, погода, настройки и загрузка ресурсов. Результат проекта — `DVSeasons.dll`.
- `DVSeasons.MP` — необязательный адаптер MultiplayerAPI. Результат проекта — `DVSeasons.Multiplayer.dll`.
- `DVSeasons.Unity` — воспроизводимые исходники AssetBundle: `Assets`, `Packages` и `ProjectSettings`.
- `DVSeasons.Tests` — модульные тесты независимой логики.
- `Resources/Runtime/AssetBundles` — единственный готовый бинарный ресурс, необходимый runtime-моду.
- `Resources/Reference` — вспомогательные визуальные материалы, которые не попадают в пакет мода.
- `Tools` — скрипты разработчика. Они не входят в устанавливаемый мод.
- `Docs` — документация проекта.

## Генерируемая часть

Весь вывод помещается в `artifacts`:

- `artifacts/build/DVSeasons` — текущая сборка для установки;
- `artifacts/releases/packages` — ZIP-релизы;
- `artifacts/releases/expanded` — проверяемое распакованное содержимое релизов;
- `artifacts/legacy` — сохранённые предыдущие сборки, резервные копии и исследовательские материалы старого рабочего каталога.

`artifacts`, `bin`, `obj`, Unity `Library`, `Logs`, `Temp`, `UserSettings`, локальный Unity `Build` и генерируемые `Texture2DArray` исключены через `.gitignore`. Их отсутствие не мешает сборке исходников.

Runtime AssetBundle намеренно хранится в `Resources/Runtime`, а не внутри C#-проекта. `DVSeasons.Game.csproj` подключает его как linked content и копирует в правильный путь пакета. Это отделяет код от ресурса без изменения структуры устанавливаемого мода.
