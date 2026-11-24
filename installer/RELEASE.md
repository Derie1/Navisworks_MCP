# Navisworks MCP Bridge @VERSION@

*Русский — ниже. English — at the end.*

---

## Что это

Плагин для Autodesk Navisworks Manage / Simulate 2026 и 2027, который даёт ИИ-ассистенту —
Claude Desktop, Claude Code, Codex или другому клиенту MCP — доступ к открытой модели. Вы пишете
задачу обычным языком — «обнови подмодели и сохрани NWD в формате 2026», «покажи новые коллизии
ОВ и назначь их подрядчику», «выгрузи отчёт по коллизиям» — ассистент выполняет её инструментами
плагина: файлы и подмодели, проверки коллизий, наборы, свойства, видовые точки, снимки вида.

Мост включается только кнопкой **AI companion → MCP Bridge** в Navisworks; связь идёт лишь внутри
компьютера (localhost).

**Новое в 2.5.0 — свойства, поиск, наборы, видовые точки, видимость:**
- Ассистент видит, какие свойства есть в модели и какие у них значения, и ищет по
  нескольким условиям сразу — «трубы больше DN 100 на этажах E01 или E02», с числами,
  длинами и углами в их единицах.
- Наборы и видовые точки — как в окнах Sets и Saved Viewpoints: папки, переименование,
  перенос, обновление, условия поискового набора в читаемом виде. Видовая точка
  сохраняет и возвращает скрытые элементы, цвета и сечение; камеру можно задать точно.
- Выгрузка свойств многих элементов в CSV для Excel.
- Скрыть, скрыть невыделенное, показать вложенные, показать всё — встроенными командами
  Navisworks. Ни один инструмент больше не перебирает всю модель: на федеративной модели
  1,9 ГБ Navisworks больше не падает.

*В 2.4.0:* положение моделей (диалог Units and Transform) — чтение, сдвиг, поворот,
масштаб, возврат в исходное положение с сохранением геопривязки IFC.

*В 2.3.0:* MCP-сервер на .NET вместо Python; установщик на русском и английском сам
подключает сервер к Claude Desktop, Claude Code и Codex.

## Требования

- Windows 10/11, 64 бит.
- Navisworks Manage или Simulate 2026 или 2027.
- .NET 8 или новее — Navisworks 2026/2027 ставит его сам; установщик проверяет.
- ИИ-клиент с поддержкой MCP: Claude Desktop, Claude Code или Codex.

## Установка

1. Закройте Navisworks.
2. Распакуйте архив и запустите `INSTALL.BAT`, выберите язык и **I** — установить.
3. Согласитесь подключить сервер к найденным ИИ-клиентам. Клиент, где уже был сервер `navisworks`,
   переключается на новый сервер без вопроса.
4. Перезапустите ИИ-клиент. Запустите Navisworks и нажмите **AI companion → MCP Bridge** — после
   каждого запуска Navisworks.

Удаление: `INSTALL.BAT`, пункт **U**. Без вопросов: `INSTALL.BAT I` или `INSTALL.BAT U`.

---

# English

## What it is

A plugin for Autodesk Navisworks Manage / Simulate 2026 and 2027 that gives an AI assistant —
Claude Desktop, Claude Code, Codex or another MCP client — access to the open model: files and
sub-models, clash tests, sets, properties, viewpoints, view captures.

The bridge starts only from **AI companion → MCP Bridge** in Navisworks and talks only inside the
computer (localhost).

**New in 2.5.0 — properties, search, sets, viewpoints, visibility:**
- The assistant sees which properties a model has and their values, and searches with
  several conditions at once — "pipes over DN 100 on levels E01 or E02", with numbers,
  lengths and angles in their units.
- Sets and viewpoints as in the Sets and Saved Viewpoints windows: folders, rename, move,
  update, search-set conditions in readable form. A viewpoint stores and brings back hidden
  items, colours and the section; the camera can be set exactly.
- Properties of many items to CSV for Excel.
- Hide, Hide Unselected, Unhide Children, Unhide All on Navisworks' own commands. No tool
  walks the whole model any more: Navisworks no longer crashes on a 1.9 GB federated model.

*In 2.4.0:* model position (the Units and Transform dialog) — read, move, rotate, scale,
put back keeping the IFC georeference.

*In 2.3.0:* the MCP server is .NET instead of Python; the installer in English and Russian
connects it to Claude Desktop, Claude Code and Codex.

## Requirements

- Windows 10/11, 64-bit.
- Navisworks Manage or Simulate 2026 or 2027.
- .NET 8 or newer — Navisworks 2026/2027 installs it; the installer checks.
- An MCP-capable AI client: Claude Desktop, Claude Code or Codex.

## Installation

1. Close Navisworks.
2. Unpack the archive, run `INSTALL.BAT`, choose the language and **I** — install.
3. Agree to connect the server to the AI clients found. A client that already had a `navisworks`
   server is switched to the new one without asking.
4. Restart the AI client. Start Navisworks and click **AI companion → MCP Bridge** — after every
   Navisworks start.

Uninstall: `INSTALL.BAT`, option **U**. Without questions: `INSTALL.BAT I` or `INSTALL.BAT U`.
