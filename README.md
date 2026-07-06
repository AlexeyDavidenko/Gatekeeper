# Gatekeeper

Сервис модерации заявок на вступление в Telegram-сообщества: пользователь подаёт заявку на вход,
проходит короткий опрос в личных сообщениях с ботом, а модераторы принимают решение — из
Telegram-группы или через отдельный веб-интерфейс.

## Как это работает

1. Пользователь отправляет join request в группу.
2. Бот открывает с ним диалог и задаёт настроенный для сообщества опрос.
3. Заполненная заявка попадает в очередь на рассмотрение — карточка приходит в админ-группу
   и доступна на сайте.
4. Модератор принимает решение (Approve/Reject) в Telegram или на сайте; бот исполняет его
   в Telegram (снимает/оставляет ограничение на вход, уведомляет пользователя).
5. Решение, принятое в Telegram, всегда имеет приоритет над решением с сайта.

Каждое сообщество (тенант) хранит данные в собственной базе данных.

## Стек

- .NET 10, C# 14
- .NET Aspire — топология и оркестрация локально/при развёртывании
- ASP.NET Core Minimal API
- Entity Framework Core + Npgsql (PostgreSQL)
- Blazor (static SSR) — веб-интерфейс модерации
- Telegram.Bot
- OpenTelemetry

## Структура решения

```
src/
  Gatekeeper.Domain           доменные сущности, инварианты, FSM заявки
  Gatekeeper.Application      сценарии (use case-хендлеры) и порты
  Gatekeeper.Infrastructure   EF Core, мультитенантность, реализации портов
  Gatekeeper.Contracts        общие DTO и интерфейс API-клиента (Api ↔ Bot ↔ Web)
  Gatekeeper.Api              ASP.NET Core API — единственный, кто ходит в БД
  Gatekeeper.Bot              воркер: long polling + исполнение исходящих команд Telegram
  Gatekeeper.Web              Blazor-интерфейс для модераторов
  Gatekeeper.ServiceDefaults  общие дефолты Aspire (health checks, OpenTelemetry)
  Gatekeeper.AppHost          топология развёртывания как код
```

## Запуск

Требуется .NET 10 SDK и PostgreSQL (локально или в Docker).

```bash
dotnet build Gatekeeper.slnx
dotnet run --project src/Gatekeeper.AppHost
```

Секреты (токен бота, ключ межсервисной аутентификации, строки подключения) задаются через
`dotnet user-secrets` или переменные окружения — в репозитории не хранятся.

## Статус

Проект в активной разработке.
