# Philosophy of Software Development

## Chapter 1: The Human Role in the AI Development Loop

People establish the purpose of development and keep it present throughout
the work through review and correction.

### 1. Development is purposeful activity

An idea expresses an intention to create something or change what already
exists. Development makes that intention concrete through activity directed
toward an anticipated result.

> A spider conducts operations that resemble those of a weaver, and a bee puts to shame many an architect in the construction of her cells. But what distinguishes the worst architect from the best of bees is this, that the architect raises his structure in imagination before he erects it in reality. At the end of every labour-process, we get a result that already existed in the imagination of the labourer at its commencement. He not only effects a change of form in the material on which he works, but he also realises a purpose of his own that gives the law to his modus operandi, and to which he must subordinate his will. And this subordination is no mere momentary act. Besides the exertion of the bodily organs, the process demands that, during the whole operation, the workman’s will be steadily in consonance with his purpose. This means close attention. The less he is attracted by the nature of the work, and the mode in which it is carried on, and the less, therefore, he enjoys it as something which gives play to his bodily and mental powers, the more close his attention is forced to be.[^labor]

AI can plan, implement, and check work. It can also help people discover
possibilities and formulate goals. The capacity to perform these activities
does not by itself determine what is worth creating or confer authority to
choose the purpose.

### 2. Review and correction sustain the purpose

An intention can drift as it passes through expression, interpretation,
design, and implementation. Locally correct work may no longer serve the
purpose for which it began.

Review determines whether the result realizes the intention. Correction
redirects the activity when it does not. Both continue the same creative
process that began with the idea. Even a technically correct result can
require correction because it does not fulfill the intended purpose.

### 3. People are the final harness

People retain the final authority over purposes and trade-offs while
delegating execution and checks. This authority does not make their judgment
infallible. Results can expose a mistaken assumption or an incomplete goal;
people also revise their understanding through development.

This role requires the actual ability to understand results, question
assumptions, and correct direction. AI can supply evidence and challenge a
proposal. People can delegate work without continuously observing every
operation, while retaining meaningful judgment over what the work serves.

## Chapter 2: Humans, Agents, and Programs

Humans use software to act on things they want to understand or change. An
Agent can undertake work on their behalf and use programs to carry out their
intentions. This activity develops through cooperation among humans, Agents,
and programs.

### 1. Humans can delegate the use of programs

A person can use a program through an Agent without specifying every
operation. Delegation can include interpreting the task, choosing tools,
connecting operations, and deciding how to continue. The Agent takes up the
person's intention as the purpose of its activity.

Its ability to sustain this activity depends on its intelligence, the means
available, and the conditions in which it acts. Planning, tool use, and
adaptation to feedback are themselves capabilities that training can
develop.[^agent-practice]

Agents are themselves implemented as programs. Using and being used describe
positions within an activity. An Agent can use programs to act, while programs
can invoke Agents and organize their execution. The same Agent can occupy
both positions.

```text diagram
+--------+ delegate  +--------+ use       +----------+
| Humans +---------->| Agents +---------->| Programs |
+--------+           +----+---+           +-----+----+
     ^         results    ^                     |
     +--------------------+          invoke     |
                          +---------------------+
```

### 2. Cooperation develops through action and feedback

An activity can cross the boundaries of several applications. Their
operations become connected through the work being done and the object being
examined or changed. In investigating an unusual expense, queries,
calculations, and comparisons of records become parts of the same inquiry.
Each program can contribute without understanding the purpose of the whole
activity.

Action changes the conditions for further action. Results can challenge an
assumption, reveal a missing step, or suggest another approach. Agents
interpret this feedback; humans can refine their intentions as the
consequences become clear. Understanding the intention and acting on it
develop together.

The Agent's interpretation also shapes which possibilities become available.
It can discover useful means or overlook them. The work therefore tests both
the chosen means and the interpretation that guided their use.

Cooperation can include sustained dialogue, extended autonomous work, and
direct manipulation of results. Humans can move between these forms within
the same activity. The appropriate form depends on what they seek from the
activity, including the value of taking part in it.

### 3. Use can transform its own means

When an Agent creates or modifies a program, a means of action becomes an
object of work. When the resulting program is used, it becomes a means again.
Using software can thus include producing the tools needed to proceed.

```text diagram
+-----------------+ put to use  +----------------+ enables     +--------------+
| Program: object +------------>| Program: means +------------>| Further work |
+-----------------+             +----------------+             +-------+------+
         ^                              create or modify               |
         +-------------------------------------------------------------+
```

An activity can leave behind programs, methods, and records that become
conditions for later work.[^labor] Existing programs carry forward knowledge
and experience from earlier development. More capable Agents can make better
use of these means, while better means expand what Agents can accomplish.

Each use takes place under particular conditions, while reusable programs
preserve relatively stable rules. Existing tools may need adjustment;
creating every tool anew would discard useful accumulated work. Choosing
when to reuse, combine, modify, or create programs is part of carrying out
the intention. Through this activity, humans and Agents can change the means
available for future use.

## References

[^labor]: Karl Marx, [*Capital*, Volume I, Chapter 7, Section 1: The Labour-Process](https://www.marxists.org/archive/marx/works/1867-c1/ch07.htm#S1).

[^agent-practice]: Moonshot AI, [*Kimi K3: Open Frontier Intelligence*](https://arxiv.org/html/2607.24653v1), sections 4.1 and 4.2; MiniMax, [*MiniMax M3: Frontier Coding, 1M Context, Native Multimodality — All in One Model*](https://www.minimax.io/blog/minimax-m3).
