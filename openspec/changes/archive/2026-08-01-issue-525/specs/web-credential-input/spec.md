### Requirement: Credentials are captured through masked inputs and never displayed in cleartext

Slack App and Bot tokens SHALL be entered through protected inputs that mask the value while typing and SHALL NOT be displayed in cleartext anywhere in the Web. The Web SHALL NOT provide a way to paste a token as a command-line argument equivalent (for example into a URL) in place of the protected form.

#### Scenario: Token fields are masked
- **WHEN** an operator types an App token or Bot token into the credential form
- **THEN** the entered value is masked and is not shown in cleartext on the page

### Requirement: Tokens are transmitted only in the request body

App and Bot tokens SHALL be transmitted to the Server only inside the body of the request that configures the Connection. Tokens SHALL NOT appear in the URL query string, the URL path, or any other part of the request location that can be logged or bookmarked.

#### Scenario: Tokens are sent in the body, not the URL
- **WHEN** the operator submits the credential form
- **THEN** the App token and Bot token are sent in the request body to the configure endpoint and are absent from the URL query string and path

### Requirement: Tokens are never persisted client-side and are never read back

The Web SHALL NOT persist tokens to the URL, session storage, local storage, or any other durable client store. After a successful submission the Web SHALL NOT retain, re-read, or display the tokens; a Connection view SHALL expose only a credential status, never the secret value. The Web SHALL NOT issue any read that returns the stored token.

#### Scenario: Tokens are not persisted to durable client storage
- **WHEN** the operator submits the credential form
- **THEN** the tokens are not written to the URL, session storage, or local storage, and no token value remains accessible to the page after the submission completes

#### Scenario: The connection view does not expose the token
- **WHEN** an operator views a Connection whose credentials have been configured
- **THEN** the view shows a credential status and does not display, echo, or offer to reveal the App token or Bot token

### Requirement: Submitted tokens are persisted only by the Server's encrypted secret store

Once submitted through the protected form, tokens SHALL be persisted solely by the Server's encrypted secret store. Tokens SHALL NOT enter the Agent's instructions, messages, logs, or any Session transcript.

#### Scenario: Tokens are stored encrypted server-side and kept out of Agent context
- **WHEN** the operator submits the credential form
- **THEN** the tokens are stored by the Server's encrypted secret store and do not appear in the Agent's instructions, messages, logs, or Session transcript
