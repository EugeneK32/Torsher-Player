# Torsher Player — Торшер Плеер

**Torsher Player** — TV-ориентированный внешний плеер для **Lampa** на **.NET 8 + Avalonia + libmpv**. Интерфейс, управление и интеграция с Lampa принадлежат самому плееру; libmpv работает внутри процесса только как медиадвижок.

> **Статус:** публичный pre-release. Windows уже основная обкатанная платформа; Linux и macOS включены в CI/release matrix, но требуют более широкого тестирования на реальном железе.

## Что умеет

- собственный масштабируемый TV-интерфейс (`0.9×`–`2.5×`, по умолчанию `1.8×`);
- автоскрытие OSD и курсора через 4 секунды реального бездействия;
- пауза, seek, громкость/mute, fullscreen;
- выбор аудиодорожки;
- выбор/отключение субтитров, задержка и масштаб;
- задержка аудио и скорость воспроизведения;
- внешние субтитры/аудиодорожки и drag & drop;
- скриншоты;
- собственный buffering/error/retry UI без системных MessageBox;
- resume/checkpoint и HTTP callback-события для Lampa;
- геймпад через SDL3, клавиатура и мышь;
- видимый directional focus: стрелки/D-pad выбирают активный элемент, Enter/A нажимают его, Esc/B возвращают назад;
- встроенное открытие `.torrent` с выбором серии/видеофайла;
- 13 сохраняемых тем, включая три светлые палитры;
- защита от одновременного запуска нескольких экземпляров;
- единый codebase для Windows, Linux и macOS.

Плеер **не** запускает внешний `mpv.exe`, не использует JSON IPC, `--wid` или стандартный OSC mpv. Видео идёт через libmpv Render API прямо в OpenGL surface Avalonia.

Для нестандартной установки native-библиотек можно задать `TORSHER_PLAYER_NATIVE_PATH`: несколько каталогов разделяются `;` в Windows и `:` в Linux/macOS.

## Платформы

| Target | CI | Native runtime |
|---|---:|---|
| Windows x64 | да | pinned libmpv + SDL3 входят в release archive |
| Windows arm64 | да | pinned libmpv + SDL3 входят в release archive |
| Linux x64/arm64 | да | системный `libmpv.so`, SDL3 опционален |
| macOS x64/arm64 | да | системный `libmpv.dylib`, SDL3 опционален |

В macOS-архиве лежит `TorsherPlayer.app`; пока он не подписывается и не notarize'ится.

## Требования для сборки из исходников

- .NET 10 SDK. Само приложение пока таргетит `net8.0`; `global.json` выбирает современный .NET 10 SDK/компилятор, необходимый для source generator Avalonia 12 и CI.
- В Windows `scripts/fetch-native.ps1` воспроизводимо скачивает libmpv и SDL3; для распаковки mpv нужен 7-Zip.
- В Linux/macOS пока требуется системный libmpv; SDL3 опционален.

## Сборка Windows

```powershell
./scripts/fetch-native.ps1 -Rid win-x64
dotnet restore src/TorsherPlayer/TorsherPlayer.csproj -r win-x64
dotnet build src/TorsherPlayer/TorsherPlayer.csproj -c Release -r win-x64
```

Или portable-сборка одной командой:

```bat
build.cmd
```

ARM64:

```powershell
./scripts/build.ps1 -Rid win-arm64 -Target portable
```

`fetch-native.ps1` скачивает зафиксированные версии libmpv/SDL3 и проверяет SHA-256. Для распаковки mpv нужен 7-Zip.

## Linux / macOS

```bash
./build.sh
```

При необходимости RID можно передать явно, например `./build.sh linux-arm64`.

## Интеграция с Lampa

Основной запуск:

```text
TorsherPlayer --url <url-or-path> --media-key <key> --title <title> ...
```

Поддерживаются callback/progress, resume, внешние sub/audio, HTTP headers, User-Agent/Referer и параметры TV UI. Полный контракт: [docs/LAMPA_INTEGRATION.md](docs/LAMPA_INTEGRATION.md).


## Управление

Основной сценарий теперь не требует запоминать скрытые хоткеи:

- **клавиатура:** стрелки перемещают видимый фокус, `Enter` нажимает активный элемент, `Esc` — назад; `Tab` тоже работает. Если в фокусе шкала прогресса, короткое `←/→` перематывает на ±10 секунд, а удержание плавно разгоняет preview-перемотку; реальный seek выполняется один раз при отпускании;
- **геймпад:** D-pad/левый стик перемещают фокус, A/South нажимает, B/East — назад;
- **мышь/тач:** обычные кликабельные кнопки, колесо меняет громкость, двойной клик переключает fullscreen, seekbar поддерживает drag.

Дополнительные быстрые команды сохранены: `Space/P` — пауза, `M` — mute, `F/F11` — fullscreen, `Q` — выход, `[`/`]` — задержка субтитров; Menu на геймпаде открывает настройки, View переключает fullscreen, бамперы дают ±60 секунд.

Активный элемент всегда получает заметную подсветку. Через четыре секунды реального бездействия OSD и курсор по-прежнему скрываются.

## Темы

В настройках доступны 13 тем: **Midnight, OLED, Ocean, Amber, Forest, Crimson, Violet, Nord, Rose, Cobalt, Light, Paper и Ivory**. `Light` — чистая холодная светлая тема, `Paper` — бумажно-графитовая со спокойным slate-акцентом, `Ivory` — тёплая кремово-янтарная. Все иконки панели берутся из одного пакета Material Design Icons и подчиняются общей палитре — вручную нарисованных SVG для кнопок нет.

## Torrent-файлы

Можно запускать напрямую:

```text
TorsherPlayer --torrent "D:\media\season.torrent"
```

или передать `.torrent` позиционным аргументом. Если в torrent несколько видеофайлов, плеер показывает экран выбора серии/файла; во время просмотра этот же список доступен отдельной кнопкой на OSD. Для автоматического выбора есть:

```text
--torrent-file-index N
```

Каждый файл получает отдельный производный `media-key`, поэтому resume/checkpoint и callback-прогресс серий не смешиваются. Torrent-функция только воспроизводит переданный пользователем файл: поиск/каталог torrent-контента в Torsher Player не встроен.

## GitHub CI / Releases

- `ci.yml` проверяет структуру репозитория и компилирует все шесть release RID;
- тег `v*` запускает `release.yml`;
- release workflow публикует `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`;
- генерируется `SHA256SUMS.txt`;
- Windows native-зависимости не хранятся бинарниками в Git: версии, URL и SHA-256 лежат в `native/native-dependencies.json`.

## Лицензия

Torsher Player публикуется под **GPL-2.0-or-later**. Детали сторонних компонентов: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
