# DeskMax

Windows-клиент по умолчанию подключается к `https://anydesk.familyserver.su/` (сервер 138.16.155.10). Адрес можно изменить в настройках. Сборка с настроенным сервером находится в `artifacts/connected-client/DeskMax.exe`; переносить нужно весь каталог.

MVP удалённого доступа для Windows 10/11 и Android.

Сейчас реализованы координационный ASP.NET Core API, уникальные шестизначные ID, HMAC-коды с периодом 30 минут, запрос подтверждения сеанса, явно выдаваемый unattended-доступ, rate limit, WPF-прототип и Docker/Coturn-каркас.

Это ещё не production-версия: состояние API пока хранится в памяти. Следующие этапы — PostgreSQL, аккаунты и 2FA, WebRTC-сигналинг, end-to-end ключи, захват/ввод Windows, Android MediaProjection и Accessibility Service.

## Обновления и Windows-сборки

Текущая версия — **0.2.0**, канал — **Stable / GitHub Releases**. При запуске клиент проверяет наличие обновления и показывает кнопку с новой версией. Установка начинается только после подтверждения пользователя. Draft и prerelease-релизы не предлагаются. Кнопка «Проверить обновления» выполняет проверку вручную.

Для следующего выпуска измените `<Version>` в проекте Windows и значение `MyAppVersion` в установщике, затем отправьте тег `vMAJOR.MINOR.PATCH`. Workflow проверит совпадение тега и версии, выполнит тесты и опубликует EXE, установщик и SHA-256 в стабильном релизе. Пароли сервера и GitHub-токены в клиент не включаются.

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
