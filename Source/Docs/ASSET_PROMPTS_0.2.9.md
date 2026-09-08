# Bitmap assets — 0.2.9

ImageGen использован для создания новой albedo-текстуры льда и зимних вариантов восьми извлечённых оригинальных материалов. Снег находится в PNG, а не в дополнительном процедурном шейдере. Геометрия и освещение игровых объектов остаются за штатными материалами.

## Файлы

Runtime: `Resources/Runtime/Textures/Seasonal/winter/`.
Unity source: `DVSeasons.Unity/Assets/DVSeasons/DV99/winter/`.
Исходные извлечённые референсы разработки: `artifacts/inspection/snow-029/` (не включены в runtime).

Сгенерированы:

- `WaterIceAlbedo.png`
- `MB_rooftile_red_01d.png`, `MB_rooftile_brown_01d.png`
- `MB_roofsheets_rusty_01d.png`, `MB_roofsheets_01d_gray.png`, `MB_roofsheets_01d_blue.png`
- `MB_rooftop_cinder_01d.png`
- `MB_concrete_rough_01d.png`
- `MB_cobblestone_pavement_01d.png`

Без генерации скопированы совместимые существующие зимние PNG: `AsphaltTiling_01d` → `AsphaltTiling_01d_White`, `MB_concrete_01d` → `MB_concrete_01d_blue`.

Результаты генератора сохранены без последующей визуальной ретуши. Размер задаётся импортом Unity и существующей загрузкой сезонных текстур под размер оригинала. Превью просмотрены; идеальное бесшовное совпадение всех границ и UV не подтверждено игровой проверкой.

## Prompt — ice

Use case: photorealistic-natural. Asset type: seamless square game PBR albedo texture, frozen lake ice. Produce one 1024x1024 flat top-down tile of smooth pale desaturated grey-blue lake ice, subtle cloudy trapped air, fine cloudy frost flecks, just a few very faint thin irregular fractures, low contrast. Neutral diffuse lighting, no baked highlights, no sun reflection, no shadows, no waves, no raised relief, no bevelled polygon cells, no dense crack web, no mirrored symmetry, no text or border. All four edges must tile seamlessly. This replaces excessively embossed cracked ice in a realistic railway simulator. It will be lit and reflected by the engine, so keep albedo low-contrast and natural.

## Prompt — each of eight winter material edits

Use case: lighting-weather. Asset type: seamless winter game albedo texture, 1024x1024 square. Input image is the edit target. Change ONLY by adding natural granular settled snow over 65-75 percent of this material, leaving irregular exposed patches and readable original seams. CRITICAL: preserve the exact original grid, seams, number of tiles/panels, orientation, framing, UV layout and size proportions; no redesign, no extra seams. Snow should look matte pale neutral grey-white, not blown-out pure white, subtle soft granularity with accumulated edges. Top-down orthographic flat diffuse light; no sun/shadows/highlights, no perspective, no text, no border. Seamless all edges for repeating game material. Keep underlying material pattern registered precisely. Material name: <name>

Здесь <name> — имя соответствующего материала MB_* из списка выше без расширения. Каждый запрос использовал исходную albedo-текстуру этого материала как единственное редактируемое изображение.
