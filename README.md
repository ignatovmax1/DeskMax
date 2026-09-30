# DeskMax

MVP удалённого доступа для Windows 10/11 и Android.

Сейчас реализованы координационный ASP.NET Core API, уникальные шестизначные ID, HMAC-коды с периодом 30 минут, запрос подтверждения сеанса, явно выдаваемый unattended-доступ, rate limit, WPF-прототип и Docker/Coturn-каркас.

Это ещё не production-версия: состояние API пока хранится в памяти. Следующие этапы — PostgreSQL, аккаунты и 2FA, WebRTC-сигналинг, end-to-end ключи, захват/ввод Windows, Android MediaProjection и Accessibility Service.

## Обновления и Windows-сборки

Windows-клиент проверяет последний GitHub Release кнопкой «Проверить обновления». Он принимает только файл `DeskMaxSetup.exe`, сверяет опубликованный SHA-256 и лишь затем запускает установщик. Workflow `.github/workflows/release.yml` автоматически собирает приложение и установщик при публикации тега `v*`.

Локальные артефакты после сборки:

- `artifacts/app/DeskMax.exe` — самостоятельное приложение;
- `artifacts/installer/DeskMaxSetup.exe` — установщик;
- `artifacts/installer/DeskMaxSetup.exe.sha256` — контрольная сумма.

## Сборка

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build DeskMax.slnx
& 'C:\Program Files\dotnet\dotnet.exe' test DeskMax.slnx
```

ID и временный код не являются достаточной авторизацией сами по себе. Обычный сеанс требует подтверждения владельца; unattended-доступ должен быть видимым, отзывным и записываться в аудит.
