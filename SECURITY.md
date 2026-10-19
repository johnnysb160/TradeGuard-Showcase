# Security and safety

## Public snapshot

This repository excludes broker credentials, account identifiers, HMAC master keys, databases, logs, tick archives, decisions, and machine-specific runtime data.

## Financial-safety notice

The project is published as an engineering case study. It is not financial advice, has no guaranteed performance, and should be used only with synthetic feeds or a broker simulator unless independently reviewed and explicitly authorized.

## Security controls

- User-only local named-pipe ACLs
- HMAC-SHA256 frame authentication
- Per-session keys and anti-replay sequence numbers
- DPAPI-protected local master key
- Mandatory stop protection in both application and Expert Advisor
- Independent real-account rejection on the MQL5 side
- Persisted risk locks and recovery context
- Storage-health checks before new entries

## Reporting

Use a private GitHub security advisory. Do not publish credentials, account details, keys, live logs, or exploit traces in a public issue.

