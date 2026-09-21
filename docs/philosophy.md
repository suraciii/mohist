# Philosophy of Software Development

Software development turns human purposes into executable systems. It
changes what people can do and the conditions under which they work.
Its results also become the materials and constraints of later development.

Development connects intentions, means, and results. AI changes the means
available and the distribution of work. Understanding that change requires
examining how purposes guide activity, how tools and cooperation reshape it,
and how its results are judged and carried forward.

## Purpose guides activity and develops through it

A person's idea expresses an intention to create something or change what
already exists. Development gives that intention a concrete form. The
purpose must continue to guide the work as people and AI agents make decisions
about requirements, design, and implementation.

> A spider conducts operations that resemble those of a weaver, and a bee puts to shame many an architect in the construction of her cells. But what distinguishes the worst architect from the best of bees is this, that the architect raises his structure in imagination before he erects it in reality. At the end of every labour-process, we get a result that already existed in the imagination of the labourer at its commencement. He not only effects a change of form in the material on which he works, but he also realises a purpose of his own that gives the law to his modus operandi, and to which he must subordinate his will. And this subordination is no mere momentary act. Besides the exertion of the bodily organs, the process demands that, during the whole operation, the workman’s will be steadily in consonance with his purpose. This means close attention. The less he is attracted by the nature of the work, and the mode in which it is carried on, and the less, therefore, he enjoys it as something which gives play to his bodily and mental powers, the more close his attention is forced to be.[^labor]

Writing down a goal does not ensure that later decisions still serve it.
Each handoff can preserve the assigned task while losing the reason for it.
A feature can satisfy its local specification yet add behavior that the
person never wanted. Keeping the connection to the goal is work that must
continue throughout delivery.

An intention, its expression, an agent's interpretation, and a finished
result can differ. A brief instruction may leave important matters
unexpressed. An agent may turn an assumption into a detailed plan without
establishing that the assumption matches the person's intention.

Review and correction continue the same purposeful activity. A person
examines whether the result serves the intended purpose and redirects work
when it does not. Correction can repair a defect, remove unnecessary work,
or revise a product choice. A technically correct result can still fail to
serve the purpose for which it was made.

The person can also learn from the result. A concrete product may reveal
that the original idea was incomplete or that a different goal would be more
useful. The purpose guides development, while development gives the person
new grounds for understanding and changing that purpose.

This keeps human judgment within the activity it judges:

> The materialist doctrine that men are products of circumstances and upbringing, and that, therefore, changed men are products of changed circumstances and changed upbringing, forgets that it is men who change circumstances and that the educator must himself be educated.[^theses]

A person can retain authority over a goal without being infallible about it.
An early result can reveal a missing need or an unnecessary feature. The
person needs to be able to revise the goal and distinguish that decision
from asking an agent to correct its implementation.

## Tools extend capacity and change the activity

In software development, existing code, behavior, and data are objects of
work. Editors, compilers, tests, and AI agents are means through which people
change them. Improving those means changes which activities people perform
and which constraints limit the whole effort.

> An instrument of labour is a thing, or a complex of things, which the labourer interposes between himself and the subject of his labour, and which serves as the conductor of his activity.[^labor]

AI can help people explore possibilities, develop solutions, and evaluate
results. Delegating these activities changes which work a person performs
and which knowledge they need. It can reduce the cost of implementation
while increasing the volume of material that needs interpretation or review.

Tools also affect the choices people can see. An agent's proposal may help
a person form an idea, rather than merely carry out a complete instruction.
That contribution should remain visible as a proposal. Greater capability
does not by itself determine who should decide which purposes to pursue.

Human attention is part of this relation. Effective delegation does not
require continuous observation of every operation. It requires enough
understanding and evidence to make informed decisions about the work. A
system that produces more material than a person can meaningfully assess
may increase activity while weakening practical control.

Tools should therefore be evaluated through the activity they enable:
what people can accomplish, what they can understand, and how they can
correct the result. These questions remain relevant as models improve.

## Cooperation must preserve the relation to the whole

Tools participate in organized work. Dividing work among people and AI agents
introduces relationships between local assignments, shared knowledge,
decisions, and the overall purpose. A collection of successful local tasks
can still produce an unsuccessful whole.

The person requesting a change, the person implementing it, and the person
using it can encounter different parts of the problem:

> But the essence of man is no abstraction inherent in each single individual. In reality, it is the ensemble of the social relations.[^theses]

A requester may know the need without knowing the implementation cost. An
implementer may satisfy a written requirement without seeing its use. An
agent can only work from the context and evidence available to it.
Cooperation has to connect these views; assigning more work does not itself
create shared understanding.

A software factory is one way to organize development. It makes assignments,
execution, evidence, and handoffs repeatable. Its organization must still be
judged by how those activities serve the intended whole. More agents, more
completed tasks, or more layers of review do not by themselves establish
that the organization works.

Coordination cannot correct a shared false premise simply by adding another
reviewer who accepts it. Nor should the existence of a process become a
reason to manufacture work for that process. The organization needs to make
its assumptions and results available for challenge.

Human interaction with AI agents belongs within this account of cooperation.
An agent can surface a contradiction, propose an alternative, or report that
it does not know. Such responses help the person form a judgment. Apparent
agreement obtained by hiding constraints weakens the basis of that judgment.

Control depends on what the person can inspect and change. If approval
presents only an agent's conclusion, the person must either trust it or
repeat the investigation. Useful supervision makes the result, its evidence,
and the decisions still open to correction understandable.

## Claims and results return to practice

A report of completion, an observed execution, a passing test, and a useful
product support different conclusions. A report is a claim to examine.
Tests establish evidence about the behavior they exercise. They do not
establish every property of the product or the value of its goal.

For example, software can correctly implement an approved requirement and
still fail in use because the requirement misunderstood the need. The
appropriate correction may then concern the product's purpose or design,
rather than another implementation patch.

Review asks what evidence supports a conclusion and whether it concerns the
actual result being delivered. It also asks whether the assumptions behind
the work remain justified. Agreement among agents and human approval can
both be mistaken; neither replaces contact with the result and its use.

Practical feedback includes failures, constraints, and uncertainty. Reporting
them truthfully lets people revise their understanding. A system that hides
them to appear successful prevents informed direction of its own work.

This return to practice connects review to the original idea. People can
accept, correct, or change the work on the basis of what it has revealed.
The decision process should preserve this relation rather than turn
completion into an internal label detached from the product.

## Results become conditions for further development

Development leaves more than a delivered feature. It leaves code,
architecture, tools, documentation, knowledge, and habits that influence the
next activity:

> Products are therefore not only results, but also essential conditions of labour.[^labor]

What made one change easy may make the next change difficult. A new rule
can preserve a useful lesson or preserve an assumption that is no longer
true. Accumulating artifacts does not guarantee accumulating understanding.

Maintenance therefore includes selection, revision, and removal. People
and agents can simplify code, retire obsolete behavior, and revise context
when evidence shows that it no longer serves the product. Keeping every
past decision active can burden future work with incompatible purposes.

A future agent inherits the code and context left by earlier work. If those
materials preserve obsolete assumptions, the next task starts from them and
can reinforce them. Useful learning changes the maintained context and
removes what no longer applies; storing more reports is not enough.

Human involvement remains important because purposes, conditions, and
judgments can change. It does not guarantee a healthy project on its own.
Its value depends on the quality of attention and the willingness to revise
both the product and the assumptions governing its development.

## References

[^theses]: Karl Marx, [*Theses on Feuerbach*](https://www.marxists.org/archive/marx/works/1845/theses/index.htm), theses 3 and 6. Translation by Cyril Smith, 2002, based on work with Don Cuckson.
[^labor]: Karl Marx, [*Capital*, Volume I, Chapter 7, Section 1: The Labour-Process](https://www.marxists.org/archive/marx/works/1867-c1/ch07.htm#S1).
