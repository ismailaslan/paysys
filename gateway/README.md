# Paysys payment gateway (phase 1)

A small service that sits between terminals and a bank (Paysys). A terminal asks the gateway for a
payment, the gateway gets a payment code from the bank, and when the payer pays, the bank tells the
gateway and the gateway pushes the result live to the merchant's page.

```
terminal --POST /v1/payments--> gateway --POST /api/gateway/payment-codes--> bank
payer pays in the bank (code + PIN)
bank --POST /v1/bank/events (signed)--> gateway --SignalR "PaymentReceived"--> merchant's Collect page
```

## Pieces

- `Gateway.Api`: the service. Own tables (`gateway.Terminals`, `gateway.Payments`, `gateway.ProcessedEvents`)
  in the same Neon database; it creates them itself on start, so there is no migration to run.
- `Gateway.Tests`: tests for signatures, replay protection and one-use tickets.

## Endpoints

Terminal (headers `X-Terminal-Id`, `X-Terminal-Key`):

- `POST /v1/payments` with `{ "amount": 12.50, "currency": "USD" }` returns `{ paymentId, code, codeExpiresAt, status }`.
  Give the code to the payer.
- `GET /v1/payments/{id}` returns the status: `Pending`, `Completed` or `Expired`.

Bank (headers `X-Bank-Code`, `X-Bank-Key`):

- `POST /v1/bank/terminals` registers a terminal and returns its key, shown once.
- `POST /v1/bank/stream-tickets` returns a one-use ticket (60 seconds) for a merchant's browser.
- `POST /v1/bank/events` is the completion message, signed with HMAC-SHA256 over `timestamp.body`
  (`X-Timestamp`, `X-Signature`); messages older than 5 minutes are refused and each event id counts once.

Browser: `/hub/merchant` (SignalR), opened with the ticket as `access_token`.

## Settings

Gateway service (Render, Docker, Dockerfile `gateway/Dockerfile`, root directory `gateway`):

| Variable | Value |
| --- | --- |
| `ConnectionStrings__Gateway` | the Neon connection string (or set `ConnectionStrings__Paysys`) |
| `Gateway__Banks__0__Code` | `PAYSYS` (any code; the Paysys API must use the same) |
| `Gateway__Banks__0__BaseUrl` | `https://paysys-hw7d.onrender.com` |
| `Gateway__Banks__0__KeyFromBank` | secret A |
| `Gateway__Banks__0__KeyToBank` | secret B |
| `Gateway__Banks__0__WebhookSecret` | secret C |
| `Cors__AllowedOrigins__0` | `https://ismailaslan.github.io` |

Paysys API (Render):

| Variable | Value |
| --- | --- |
| `Gateway__BaseUrl` | the gateway's public address |
| `Gateway__BankCode` | `PAYSYS` |
| `Gateway__OutboundKey` | secret A |
| `Gateway__InboundKey` | secret B |
| `Gateway__WebhookSecret` | secret C |

Make A, B and C long and random, and different from each other. Without these settings Paysys
runs as before and the Collect page keeps refreshing every 3 seconds.

## Limits of phase 1

- A payment is matched to the terminal's request by terminal, amount and currency. The payer still
  types the amount in Paysys, so a payer can pay a different amount: the merchant is told the real
  amount, and the request stays pending.
- State is in memory (tickets, the hub, the bank's message queue): one instance of each service,
  and a restart loses what was waiting. The status endpoint catches up.
- Render's free tier sleeps: the first request after a pause can take 30 to 60 seconds.
- Not in phase 1: card-present charging (a terminal sending a card and PIN through the gateway).
