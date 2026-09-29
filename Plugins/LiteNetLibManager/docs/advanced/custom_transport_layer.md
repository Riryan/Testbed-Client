# Transport Layer

This MMO testbed intentionally supports a single production transport:

- **LiteNetLib UDP**

WebSocket and the previous mixed LiteNetLib/WebSocket transport have been
removed from the supported code surface. This keeps the authoritative MMO
runtime, security review, load testing, and deployment assumptions focused on
one transport implementation.

WebGL is therefore not a supported target for this testbed.
