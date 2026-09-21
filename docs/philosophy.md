# Philosophy of Software Development

Software development turns human intentions into programs that can be used.
The resulting programs also change the conditions of further work. When
Agents undertake development and use on a person's behalf, these relations
take new forms.

## Chapter 1: The Human Role in the AI Development Loop

### 1. Development is purposeful activity

Development begins with a need and with conditions in which something can be
changed. An idea gives this change an anticipated form. The purpose connects
the work to a result and guides the choice of means through which that result
might be produced. Existing software, available knowledge, and practical
constraints already enter into the formation of the idea.

> A spider conducts operations that resemble those of a weaver, and a bee puts to shame many an architect in the construction of her cells. But what distinguishes the worst architect from the best of bees is this, that the architect raises his structure in imagination before he erects it in reality. At the end of every labour-process, we get a result that already existed in the imagination of the labourer at its commencement. He not only effects a change of form in the material on which he works, but he also realises a purpose of his own that gives the law to his modus operandi, and to which he must subordinate his will. And this subordination is no mere momentary act. Besides the exertion of the bodily organs, the process demands that, during the whole operation, the workman’s will be steadily in consonance with his purpose. This means close attention. The less he is attracted by the nature of the work, and the mode in which it is carried on, and the less, therefore, he enjoys it as something which gives play to his bodily and mental powers, the more close his attention is forced to be.[^labor]

The anticipated result has to be sustained through work on an actual object.
An intention expressed at the outset can lose its force as it passes through
interpretation, design, and implementation. Each operation may satisfy its
immediate requirement while the resulting software fails to serve the
purpose that brought the operations together.

AI can plan, implement, and check work. It can also help humans discover
possibilities and formulate goals. These capacities enlarge the means
available to development. Their existence does not by itself determine what
is worth creating or confer authority to choose the purpose.

### 2. Review and correction sustain the purpose

The result makes an intention available for examination in a new form.
Humans can compare what they sought with what has been built and with what
happens when it is used. A successful check establishes something about the
behavior it examines. Whether that behavior serves the purpose remains a
question about the activity as a whole.

A discrepancy can expose a defect in the implementation, an unsuitable means,
or a mistaken understanding of the need. Correction must reach the relation
that produced it. More faithful execution of an unsuitable design can
preserve the very problem that development was meant to address.

Review and correction therefore continue the purposeful activity that began
with the idea. Its results act back on the understanding that directed it.
Human understanding develops within the practice it seeks to guide.[^practice]
The purpose can become more precise, or change, as humans encounter the
consequences of trying to realize it.

### 3. Humans are the final harness

Humans retain final authority over purposes and trade-offs while delegating
execution and checks. This authority places judgment within the work: humans
must be able to understand results, question assumptions, and redirect the
activity. An approval alone cannot establish that a result is useful, and a
human judgment can itself require correction.

AI can supply evidence and challenge a proposal. Humans can delegate work
without continuously observing every operation. The extent of delegation and
the form of participation can change while humans retain meaningful judgment
over what the work serves.

The human role is exercised through this continuing relation to purposes,
means, and results. Its effectiveness depends on the actual conditions for
judgment and correction. This leads to the question of how humans can use
programs through Agents while remaining participants in the activity.

## Chapter 2: Humans, Agents, and Programs

### 1. Humans can delegate the use of programs

A person can use a program through an Agent without specifying every
operation. The Agent can interpret the task, choose tools, connect operations,
and decide how to continue. The person's use proceeds through the Agent's
direct use of programs. These are related positions within the same activity.

The Agent takes up the person's intention as the purpose of its work. It may
still misunderstand that intention or choose unsuitable means. Its ability
to sustain the activity depends on its intelligence, the means available, and
the conditions in which it acts. Planning, tool use, and adaptation to
feedback are themselves capabilities that training can develop.[^agent-practice]
The representative role and the capacity to fulfill it must be examined
together without treating one as proof of the other.

Agents are themselves implemented as programs. Using and being used describe
positions within an activity. An Agent can use programs to act, while programs
can invoke Agents and organize their execution. The same Agent can occupy
both positions.

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

An activity can cross the boundaries of several applications. Repairing a
software defect can involve a repository, an editor, a test system, and a
review tool. Their operations become parts of one activity through the
problem being investigated and changed. Each program can contribute without
understanding the purpose of the whole activity.

Action changes the conditions for further action. Results can challenge an
assumption, reveal a missing step, or suggest another approach. Agents
interpret this feedback; humans can refine their intentions as the
consequences become clear. The understanding of the task develops through
the attempt to carry it out.

The Agent's interpretation also shapes which possibilities become available.
It can discover useful means or overlook them. Its choice of tools affects
what can be attempted, and its account of results affects what the person
can judge. Cooperation therefore tests both the chosen means and the
interpretation that guides their use.

When an Agent connects a person's intention to program operations and brings
results back into their cooperation, it acts as a user interface. This
relation can include dialogue, graphical presentation, direct manipulation,
and extended autonomous work. Humans can move between these forms according
to the purpose and conditions of the activity. Participation can itself have
value in learning and creation; a greater capacity to delegate enlarges the
available choices.

### 3. Use can transform its own means

An attempt to proceed can reveal that an available program is unsuitable or
that a needed capability does not yet exist. The activity then turns toward
its own means. When an Agent creates or modifies a program, the program is
an object of work. When the resulting program is used, it serves as a means.
These roles arise from its relation to the activity.

```text diagram
+-----------------------+ object  +---------+ means   +--------------+
| Creating or modifying +-------->| Program +-------->| Further work |
+-----------------------+         +---------+         +-------+------+
                                       ^          may modify  |
                                       +----------------------+
```

Programs, methods, and records can carry the results of earlier work into
later activity.[^labor] Their use gives access to accumulated knowledge and
experience. More capable Agents can make better use of these means, while
better means expand what Agents can accomplish. Using software can thus
include producing the tools needed to continue.

Each use takes place under particular conditions, while reusable programs
preserve relatively stable rules. A rule that supports one activity may need
adjustment in another. Recreating every tool would discard useful accumulated
work; treating every existing tool as fixed would prevent its improvement.
Choosing when to reuse, combine, modify, or create programs belongs to
carrying out the intention. Applications enter this changing relation as
means whose organization can itself become a problem for development.

## Chapter 3: Agent-Native Applications

### 1. A changed relation of use

An application brings together capabilities for work within a particular
scope. The user's activity can extend beyond that scope. In repairing a
defect, someone must connect the problem, the code change, the checks, and
the proposed delivery. Where humans operate each application directly, they
also maintain much of this connection between applications.

An interface makes capabilities available in a form its user can understand
and operate. It also gives use a particular path. A person must learn that
path, translate an intention into its operations, and relate the results to
the wider activity. The interface both enables action and establishes
conditions under which the application can be used.

Programmatic interfaces have long allowed some of this work to pass into
programs. Agents extend the work that can be delegated to include interpreting
a request and choosing how to proceed in light of results. The user can
carry an intention into several applications through the Agent's continuing
activity. Each application participates by making its capabilities available
to that use.

An Agent-native application takes the user's Agent as its primary direct
user and organizes its capabilities and interaction around that relation.
The application may itself provide an Agent, and other Agents may use its
capabilities. Being the user's Agent describes a representative role in the
activity; it does not depend solely on which product supplies the software.

This priority must take practical form in how capabilities are discovered,
understood, used, and examined. Existing command-line tools and APIs can
supply many of its conditions. Adding an Agent to a product does not by
itself establish the relation. The question is how the product's capabilities
enter the work that an Agent undertakes for its user.

### 2. Why this relation changes application design

When users delegate the organization of work to Agents, applications become
means within that delegated activity. An operation may remain useful while
the established path to it becomes an obstacle. An Agent can repeat the steps
of an interface designed for direct human operation, but those steps can
also carry avoidable work into every attempt to use the capability.

Development must examine what these conditions contribute. A real conflict
between code changes requires a decision before a merge can be completed.
Navigating several pages to discover whether a conflict exists belongs to a
particular presentation of that condition. The two requirements have
different grounds. Changing the presentation can improve access to the
capability while preserving the need to handle the conflict.

The reason to develop an Agent-native application lies in improving this
relation between a user's activity and the means available to it. Users can
carry their purposes across applications with less repeated explanation and
manual connection of operations. Agents can combine capabilities according
to the work in progress, while applications supply the knowledge and
operations already organized within them.

These benefits depend on the activity and the Agent's capacity to undertake
it. They must be established through use. Delegation also changes how humans
receive information and encounter possible courses of action. Reducing
manual steps can coexist with a loss of understanding if the consequences
become harder to inspect. An Agent-native design must therefore consider
what enables both effective action and informed human participation.

### 3. Constructing applications for delegated use

The user's Agent approaches an application from within a task whose whole
course the application cannot prescribe. It must be able to relate the
available capabilities to that task. Agent interaction therefore has priority
in the application's normal use. Its interfaces serve Agents first by
providing meaningful operations that they can select and carry out. The
meaning includes what an operation can change, when it is applicable, and
what its outcome permits next. Internal complexity can remain within the
application while these conditions become available to its users.

These capabilities must first become known to the Agent that needs them.
Descriptions and instructions connect their availability with an understanding
of their use. They must make each capability's purpose, scope, and conditions
recognizable in a concrete task. Typical methods can preserve experience
while leaving the Agent able to judge whether those methods fit the present
circumstances.
A particular description format or protocol is a means of establishing this
relation, not its definition.

Using a capability changes the conditions under which the work continues.
Action must therefore return evidence of what happened. Acceptance of a
request, completion of an operation, and fulfillment of a purpose are different
claims. A build can pass while the defect remains. The application must make
its state and the effects of operations available for examination, including
failures and uncertainty about an outcome. The Agent can then relate these
facts to the purpose and determine how to continue. Its account of progress
remains open to correction by the results it describes.

The person also needs access to evidence through which the work can be
understood and judged. Views for humans therefore emphasize the relationships,
progress, evidence, and results that support this participation. A code
comparison, a dependency view, or direct manipulation of a result can itself
contribute to the activity.
The application can provide a graphical interface, a link to a result, or
material an Agent presents in the current conversation. The form follows
what humans need to perceive and do, including direct participation.

The application retains the task of organizing reusable capabilities. Its
internal work can include Agents as well as other programs. Those
capabilities must remain useful across purposes and circumstances that the
application cannot fully prescribe. Reliable organization within the
application and flexibility in its use support one another when the
conditions and consequences of action can be understood.

The developer must therefore examine the complete relation: how a purpose
enters the work, how an Agent gains command of the available means, and how
results inform what follows. The value of an Agent-native application must
be shown in the activity it supports. Its development succeeds when the
changed relation makes software more effective as a means through which
humans can act, understand the results, and change direction.

## References

[^labor]: Karl Marx, [*Capital*, Volume I, Chapter 7, Section 1: The Labour-Process](https://www.marxists.org/archive/marx/works/1867-c1/ch07.htm#S1).

[^practice]: Karl Marx, [*Theses on Feuerbach*](https://www.marxists.org/archive/marx/works/1845/theses/index.htm), theses 2 and 3, translated by Cyril Smith.

[^agent-practice]: Moonshot AI, [*Kimi K3: Open Frontier Intelligence*](https://arxiv.org/html/2607.24653v1), sections 4.1 and 4.2; MiniMax, [*MiniMax M3: Frontier Coding, 1M Context, Native Multimodality — All in One Model*](https://www.minimax.io/blog/minimax-m3).
