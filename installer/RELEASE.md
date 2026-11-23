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

**Новое в 2.3.0:**
- MCP-сервер теперь на .NET (`navis_mcp_server.dll`) вместо Python: Python ставить не нужно.
- Новый установщик на русском и английском: ставит плагин, удаляет старый Python-сервер и сам
  подключает сервер к Claude Desktop, Claude Code и Codex.
- Сервер проверяет, что на порту 8765 отвечает именно мост Navisworks.

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

**New in 2.3.0:**
- The MCP server is now .NET (`navis_mcp_server.dll`) instead of Python: no Python needed.
- A new installer in English and Russian: installs the plugin, removes the old Python server and
  connects the server to Claude Desktop, Claude Code and Codex.
- The server checks that port 8765 is served by the Navisworks bridge.

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
