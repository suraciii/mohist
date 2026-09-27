# Philosophy of Software Development

Mohist exists to support a shift from workshop-style software development
to industrial, automated software production. In the former, people organize
and advance the work for each requirement. In the latter, executable
workflows organize Agents and programs to carry out that work.

In this development model, people use Agents and other tools to build,
maintain, and control the development process itself. The change is in how
software development is organized, not only in who writes code or how fast.
The [Product Vision](vision.md) describes how Mohist supports this model.

## From Workshop-Style Development to Automated Production

In workshop-style development, people perform or delegate tasks and arrange
their handoffs for each requirement. Tools can automate individual operations,
but people still assemble the context, evaluate intermediate results, and
decide which work follows. An Agent can write more code while this way of
organizing development remains unchanged.

The industrial form makes the development process itself executable.
Requirements enter defined workflows; Agents and programs perform the work,
evaluate results, handle feedback, and deliver software. The process can
continue with further requirements without being assembled again through
human instructions for each task.

The distinction is not whether development uses automation. It is whether
automation covers only individual operations or also the dependencies,
handoffs, evaluation, and correction that connect them. This is the shift
from workshop-style work to an automated requirement-to-delivery process.

## Why This Development Model Needs Mohist

An Agent can perform a task without providing the process that connects a
requirement to delivered software. That process needs executable definitions,
coordinated execution, verifiable results, and a way to improve its methods.

Mohist supports the construction, operation, and maintenance of this
automated development process. People use it to define workflows, provide
execution resources, inspect results, and change how later work proceeds.
These capabilities serve the new production model rather than merely
helping people coordinate each task more efficiently.

## People Build and Maintain Workflows

A workflow expresses how work depends on other work, which artifacts must be
shared, what evidence permits progress, and how feedback leads to further
action. Agents and programs can then carry out these relationships without
a person arranging each handoff again.

People develop both software and the workflows used to produce it. When
people construct a workflow, the workflow is a development result. When it
runs, it becomes a means of implementing requirements. Its results then
provide evidence for improving that method. A workflow must itself be
developed, verified, and maintained.

The workflow does more than preserve a sequence of commands. It makes a
development method available for later requirements. A shared interface
contract, for example, must reach the tasks that depend on it, and the
resulting implementations must be evaluated together when the requirement
depends on their interaction.

This does not require one sequence for all development. A defect investigation
can produce an explanation before a repair is known. A feature implementation
can use an established interface and proceed with less exploration. Workflow
definitions must express the work that the requirement needs, not impose
fixed stage names or reproduce a list of human job titles.

An Agent is also an interface through which people construct, inspect, and
change workflows. In that role it is their tool; within a workflow, it is
an autonomous executor.

### Defined Execution Must Leave Room for Discovery

Automation needs explicit inputs, dependencies, and completion conditions.
Yet a requirement does not always contain everything that implementation
will reveal. Treating every unknown as a reason to wait for a person makes
routine execution manual again. Letting an executor resolve every unknown by
changing the intended behavior makes the result unreliable in another way.

Agents can investigate, experiment, and choose an implementation within the
requirement's scope. A finding that requires a change to that scope, intended
behavior, or accepted trade-offs returns to people for a requirement decision.

Thus, a reusable workflow need not prescribe every technical choice. It
preserves the constraints and feedback relationships that matter while
allowing judgment within them. Useful methods can become stable without
making every requirement identical.

### Delivery Tests Both the Software and the Method

A completed task is not proof that a requirement is satisfied. Components
that pass separate tests can still fail when used together. Delivery evidence
must cover the required behavior of the combined software and identify the
requirement and version it evaluates.

Different findings require different corrections. If an implementation
violates an agreed interface, repair that implementation. If related tasks
keep receiving inconsistent interfaces, improve how the workflow shares and
checks the agreement. If the agreed behavior itself does not meet the
business need, revise the requirement rather than treating the problem as
another coding failure.

Corrections to the method also need evaluation. Fixing the current result
does not prove that later work will improve. A workflow change is useful
when comparable work produces better results or needs less avoidable
correction, not merely when it adds steps or executions.

## Two Loops Organize Automated Software Production

The inner loop receives requirements, executes the selected workflows, and
returns delivery results. It continues with further requirements or waits.
Agents are autonomous parts of this execution: they perform tasks, evaluate
evidence, and handle feedback within the workflow's rules. Verification and
repair are local activities in this loop, not its entire purpose.

The outer loop belongs to people. They use Agents and other tools to analyze
the business, accept delivered software, organize further requirements, and
build, maintain, and control workflows. Here the Agent is a tool for their
work, not a separate product decision-maker.

The outer loop uses both successful results and failures. A useful feature
can reveal the next business need. A recurring integration problem can
reveal a weakness in the workflow. People revise their understanding from
these results instead of assuming that the original requirement and method
were complete. The updated requirements and methods then guide later
execution.

## Humans Are the Final Harness

A workflow evaluates results within its requirements and evaluation rules.
It cannot establish that those rules cover every relevant business need
merely by satisfying them. Even when execution and verification proceed
automatically, the intended behavior, acceptance criteria, and development
method must remain open to examination and correction.

Humans are the final harness for software development. People retain final
judgment and responsibility for the product goals, acceptance criteria, and
whether the workflow remains suitable for the work. They use Agents and
other tools to examine both the delivered software and the conditions under
which the automated system judged it complete.

This role requires practical control, not a declaration. People must be able
to inspect requirements, definitions, artifacts, and execution evidence;
change requirements or development methods; and stop or redirect unsuitable
execution. For example, checks may pass because a required user interaction
was never represented in them. The appropriate response can be to correct
the acceptance criteria and workflow, not simply run the same checks again.

Final judgment does not require a human approval step after every workflow
execution, nor does it reserve people only for failures that automation cannot
repair. Routine decisions and reviews can be delegated within defined
boundaries. People continue to judge the goals and methods through business
analysis and product acceptance even when the inner loop runs successfully.

People must also revise their own judgments when execution evidence or
software use contradicts them. Final authority does not make a judgment
correct; it keeps the goals and methods of automatic execution open to
correction.
