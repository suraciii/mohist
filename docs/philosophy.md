# Philosophy of Software Development

## Chapter 1: The Human Role in the AI Development Loop

### 1. Development is purposeful activity

Development begins with a need and with conditions in which something can be
changed. An idea gives this change an anticipated form. The purpose guides
the choice of means, while existing software, knowledge, and constraints
shape what can be attempted.

> A spider conducts operations that resemble those of a weaver, and a bee puts to shame many an architect in the construction of her cells. But what distinguishes the worst architect from the best of bees is this, that the architect raises his structure in imagination before he erects it in reality. At the end of every labour-process, we get a result that already existed in the imagination of the labourer at its commencement. He not only effects a change of form in the material on which he works, but he also realises a purpose of his own that gives the law to his modus operandi, and to which he must subordinate his will. And this subordination is no mere momentary act. Besides the exertion of the bodily organs, the process demands that, during the whole operation, the workman’s will be steadily in consonance with his purpose. This means close attention. The less he is attracted by the nature of the work, and the mode in which it is carried on, and the less, therefore, he enjoys it as something which gives play to his bodily and mental powers, the more close his attention is forced to be.[^labor]

### 2. Review and correction sustain the purpose

An intention can drift as it passes through interpretation, design, and
implementation. Each operation may satisfy its immediate requirement while
the resulting software fails to serve the purpose that brought them together.
Review compares what was sought with what was built and what happens in use.

A discrepancy can expose a defect, an unsuitable means, or a misunderstood
need. Correction must reach the relation that produced it. The result can
therefore change both the software and the understanding that directed its
development. Purpose becomes more precise, or changes, through the attempt
to realize it.[^practice]

### 3. Humans are the final harness

Humans retain final authority over purposes and trade-offs while delegating
execution and checks. AI can help formulate goals, supply evidence, and
challenge proposals; its capacity to act does not itself confer authority
to decide what is worth doing.

This human role requires an actual ability to understand results, question
assumptions, and redirect work. Human judgment remains open to correction.
Delegation need not require continuous observation of every operation.

## Chapter 2: Humans, Agents, and Programs

### 1. Humans can delegate the use of programs

A person can use programs through an Agent that interprets the task, chooses
tools, connects operations, and decides how to continue. The person's use
proceeds through the Agent's direct use. Fulfilling the intention depends on
the Agent's intelligence, available means, and conditions of action. Planning,
tool use, and adaptation to feedback can themselves develop through
training.[^agent-practice] Delegation does not guarantee its fulfillment.

Agents are implemented as programs. They can use programs, while programs can
invoke Agents and organize their execution. Using and being used describe
positions within an activity; the same Agent can occupy both.

Delegated use:

```text diagram
+--------+ delegate  +--------+ use       +----------+
| Humans +---------->| Agents +---------->| Programs |
+--------+           +----+---+           +----------+
     ^         results    |
     +--------------------+
```

Program invocation:

```text diagram
+----------+ invoke  +--------+
| Programs +-------->| Agents |
+----------+         +--------+
```

### 2. Cooperation develops through action and feedback

Repairing a defect can involve a repository, an editor, a test system, and
a review tool. Their operations form one activity through the problem being
investigated and changed. Each program can contribute without understanding
the purpose of the whole.

By selecting tools and interpreting results, the Agent shapes which paths
humans can see. Action and feedback test its interpretation as well as its
chosen means. Humans and Agents can revise their understanding through this
cooperation, without having to settle every step in advance.

An Agent acts as a user interface when it connects human intention to program
operations and brings results back into the cooperation. Dialogue, graphics,
direct manipulation, and autonomous work can all participate. Learning and
creation may give direct participation a value of its own; greater capacity
to delegate enlarges the available choices.

### 3. Programs give rules an executable form

A report combines judgments about its content with rules for its form.
An Agent can organize the material into parameters; a program can validate
them and render the required format. The format becomes a property of the
means used to produce the report, rather than a requirement the Agent must
continually remember while generating it.

The same change can organize actions. A workflow engine can require a passing
verification result before allowing the next stage. An Agent can write the
workflow, whose code can branch on new results while preserving required
dependencies.[^orchestration] Work can remain dynamic while its conditions of
advancement are enforced.

Such regularity depends on a boundary. A deterministic state machine produces
the same transitions from the same initial state and ordered inputs, even
though observations from clocks, networks, or storage may vary between
runs.[^determinism] In an Agent system, judgments can likewise enter as inputs.
Making these inputs explicit allows rules to be examined and tested separately
from their changing sources. It does not make the whole activity predictable.

These guarantees cover only rules correctly implemented on operations the
program controls. Correct formatting does not establish truthful content,
nor does running a review establish a sound judgment. The adequacy of the
rules remains a question for the activity they serve.

### 4. Use can transform its own means

When an available program is unsuitable, development can become part of use.
The program is an object while it is created or modified, and a means when
it is used. These roles arise from its relation to the activity.

```text diagram
+-----------------------+ object  +---------+ means   +--------------+
| Creating or modifying +-------->| Program +-------->| Further work |
+-----------------------+         +---------+         +-------+------+
                                       ^          may modify  |
                                       +----------------------+
```

Programs, methods, and records carry earlier work into later activity.[^labor]
More capable Agents can make better use of these means, while better means
expand what Agents can accomplish.

Reusable programs preserve relatively stable rules; each use has particular
conditions. Recreating every tool discards useful work, while treating every
tool as fixed prevents improvement. Choosing when to reuse, combine, modify,
or create belongs to carrying out the intention.

## Chapter 3: Agent-Native Applications

### 1. A changed relation of use

An Agent-native application takes the user's Agent as its primary direct
user and organizes its capabilities and interaction around that relation.
It may provide an Agent itself and serve Agents from other products. Being
the user's Agent describes its representative role in the activity.

This priority takes practical form in how capabilities are discovered,
understood, used, and examined. Existing command-line tools and APIs can
supply many of these conditions. Adding an Agent to a product does not by
itself establish them: the capabilities must enter the work undertaken for
the user.

### 2. Why this relation changes application design

An interface makes capabilities available while prescribing a path to them.
When an Agent undertakes the work, that path can impose steps that contribute
little to the task. A merge conflict requires a decision; navigating several
pages to discover the conflict belongs to a particular presentation of it.
Changing the presentation can improve access while preserving the need to
handle the conflict.

Agent-native design seeks to bring capabilities into delegated work with
less repeated explanation and manual coordination. Its value depends on the
activity it supports: fewer manual steps can coexist with less understanding
if consequences become harder to inspect. Effective action and informed
participation both matter.

### 3. Constructing applications for delegated use

To select a capability, an Agent must understand what it can change, when it
is applicable, and what its outcome permits next. Application interfaces serve
Agents first through directly usable operations that enforce their declared
preconditions. Descriptions and instructions explain their use; typical methods
help the Agent apply them to concrete tasks.

Using a capability changes the conditions for further work. The application
must expose its state and evidence of effects, failures, and uncertainty.
The Agent can then judge how to continue. Its account of progress remains
open to correction by the facts it describes.

The person directing the work also needs access to this evidence. Human UI
therefore emphasizes relationships, progress, and results that support
understanding and judgment. Graphical views and direct controls can remain
part of that participation. Their form follows what the activity requires
humans to perceive and do.

## References

[^labor]: Karl Marx, [*Capital*, Volume I, Chapter 7, Section 1: The Labour-Process](https://www.marxists.org/archive/marx/works/1867-c1/ch07.htm#S1).

[^practice]: Karl Marx, [*Theses on Feuerbach*](https://www.marxists.org/archive/marx/works/1845/theses/index.htm), theses 2 and 3, translated by Cyril Smith.

[^agent-practice]: Moonshot AI, [*Kimi K3: Open Frontier Intelligence*](https://arxiv.org/html/2607.24653v1), sections 4.1 and 4.2; MiniMax, [*MiniMax M3: Frontier Coding, 1M Context, Native Multimodality — All in One Model*](https://www.minimax.io/blog/minimax-m3).

[^orchestration]: Anthropic, [*Orchestrate subagents at scale with dynamic workflows*](https://platform.claude.com/cookbook/claude-agent-sdk-08-dynamic-workflows); pi-subagents, [*Workflows and orchestration*](https://github.com/nicobailon/pi-subagents/blob/main/docs/workflows.md), sections on scripted workflows and parallel sequential lanes.

[^determinism]: Outdata, [*Deterministic Core, Non-Deterministic Shell*](https://outdata.net/blog/260803), August 3, 2026.
