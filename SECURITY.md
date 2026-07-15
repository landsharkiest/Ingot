# Security Policy

## Supported versions

Ingot is pre-1.0. Security fixes are applied to the latest published `0.x` release on
[NuGet](https://www.nuget.org/packages/Ingot). Please make sure you can reproduce an issue on the
latest version before reporting it.

| Version | Supported |
| ------- | --------- |
| 0.1.x   | ✅        |
| < 0.1   | ❌        |

## Reporting a vulnerability

**Please do not open a public issue for security vulnerabilities.**

Report privately through GitHub's
[private vulnerability reporting](https://github.com/landsharkiest/Ingot/security/advisories/new)
(Security → Report a vulnerability). Include:

- a description of the issue and its impact,
- the affected version,
- a minimal reproduction, and
- any known mitigations.

We aim to acknowledge reports within a few business days and will keep you informed as we
investigate and prepare a fix. Once a fix is released, we're happy to credit you in the advisory
unless you prefer to remain anonymous.

## Scope

Ingot is middleware over an `IChatClient`; it does not manage provider credentials or make network
calls of its own. When reporting, focus on Ingot's own behavior — for example, unsafe handling of
model output, validation bypasses, or logging that leaks payloads despite `RedactPayloads`. Issues
in provider SDKs or the underlying `Microsoft.Extensions.AI` packages should be reported to their
respective maintainers.
