# Interaction Design

## Session Boundary

Slack organizes one conversation as a thread, so Mohist uses `Agent + thread`
as the Session boundary instead of sharing one context forever across a
channel. Three rules:

- One thread may contain multiple AgentSessions, one per Agent. Mentioning a
  new Agent neither switches nor contaminates the original Session.
- Do not start work when ownership is ambiguous. A multi-Bot mention, or an
  unmentioned reply in a multi-bound thread, gets an interactive chooser
  instead of a guess.
- Do not start work when the target Agent cannot run it. Server posts one
  guidance message — safe summary for the caller, specific gap only for Owner
  and operators — without creating a Session, Turn, or queued input,
  deduplicated per triggering message.

DM is the exception: Slack DM users do not organize work by thread. Server
stores one current AgentSession per Connection DM conversation; every normal
message continues it. `new task <prompt>` is the explicit opt-in to an
independent AgentJob and Session; infrastructure recovery must never require
that grammar. Parallel work can use separate channel threads.

DM continuation is fail-closed:

```text diagram
                              +----------------+
                              | new DM message |
                              +--------+-------+
                                       |
                                       v
                              +----------------+
                              | binding state? |
                              +--------+-------+
       +---------------------+---------+----------+-------------------+
       vlaunch pending       vretry-safe terminal vidle + missing     vactive / unknown / stale
+-------------------+   +---------------+   +-----------------+  +--------------+
| ordered follow-up |   | durable retry |   | replace Session |  | never replay |
+-------------------+   +---------------+   +-----------------+  +--------------+
```

The inbox route is the crash-recovery fence: a retry must resolve its durable
replacement before route migration, the conditional route update prevents a
concurrent redelivery from overwriting a newer Session, and the follow-up's
stable Slack idempotency key prevents duplicate SessionInput records after the
migration. Different Mohist Servers never share thread routing.
