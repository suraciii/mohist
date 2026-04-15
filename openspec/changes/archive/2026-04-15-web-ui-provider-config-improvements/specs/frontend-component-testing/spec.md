## ADDED Requirements

### Requirement: Frontend testing infrastructure
The system SHALL provide testing infrastructure for React components using React Testing Library and Vitest.

#### Scenario: Test utilities available
- **WHEN** a developer writes a component test
- **THEN** the system SHALL provide render(), screen, fireEvent, and waitFor utilities from React Testing Library

#### Scenario: Query client provider wrapper
- **WHEN** a component uses TanStack Query hooks
- **THEN** the test infrastructure SHALL provide a QueryClient wrapper for tests

#### Scenario: Mock service worker setup
- **WHEN** a component makes API calls
- **THEN** the test infrastructure SHALL support MSW (Mock Service Worker) for API mocking

### Requirement: SettingsPage component tests
The system SHALL provide comprehensive tests for the SettingsPage component.

#### Scenario: SettingsPage renders providers tab
- **WHEN** the SettingsPage is rendered
- **THEN** it SHALL display the Providers tab with connected and available provider sections

#### Scenario: SettingsPage switches tabs
- **GIVEN** the SettingsPage is on the Providers tab
- **WHEN** user clicks the General tab
- **THEN** the General tab content SHALL be displayed

#### Scenario: SettingsPage displays loading state
- **GIVEN** providers data is loading
- **WHEN** the SettingsPage is rendered
- **THEN** it SHALL display loading placeholders/skeletons

#### Scenario: SettingsPage displays error state
- **GIVEN** providers query fails
- **WHEN** the SettingsPage is rendered
- **THEN** it SHALL display an error message

### Requirement: ProviderConnectDialog component tests
The system SHALL provide tests for the ProviderConnectDialog component.

#### Scenario: Dialog renders with provider info
- **GIVEN** a provider is selected
- **WHEN** the ProviderConnectDialog opens
- **THEN** it SHALL display the provider name and description

#### Scenario: API Key input validation
- **GIVEN** the ProviderConnectDialog is open
- **WHEN** user enters an empty API Key and tries to save
- **THEN** the save button SHALL be disabled

#### Scenario: Test Connection button enables with input
- **GIVEN** the ProviderConnectDialog is open
- **WHEN** user enters a valid API Key
- **THEN** the Test Connection button SHALL become enabled

#### Scenario: Dialog shows test success
- **GIVEN** a valid API Key is entered
- **WHEN** user clicks Test Connection and it succeeds
- **THEN** a success message with green checkmark SHALL be displayed

#### Scenario: Dialog shows test failure
- **GIVEN** an invalid API Key is entered
- **WHEN** user clicks Test Connection and it fails
- **THEN** an error message with red X SHALL be displayed

### Requirement: CustomProviderDialog component tests
The system SHALL provide tests for the CustomProviderDialog component.

#### Scenario: Form validation for provider ID
- **GIVEN** the CustomProviderDialog is open
- **WHEN** user enters an invalid provider ID (special characters)
- **THEN** an error message SHALL be displayed

#### Scenario: Form validation for base URL
- **GIVEN** the CustomProviderDialog is open
- **WHEN** user enters an invalid URL
- **THEN** an error message SHALL be displayed

#### Scenario: Save button disabled with invalid form
- **GIVEN** the form has validation errors
- **WHEN** user tries to save
- **THEN** the save button SHALL be disabled

#### Scenario: Warning dialog shows without test
- **GIVEN** all fields are valid but Test Connection was not clicked
- **WHEN** user clicks Save
- **THEN** a warning dialog SHALL appear recommending to test first

#### Scenario: Pre-save warning allows override
- **GIVEN** the pre-save warning is displayed
- **WHEN** user clicks "Save Anyway"
- **THEN** the provider SHALL be saved

#### Scenario: All form fields collect input
- **GIVEN** the CustomProviderDialog is open
- **WHEN** user fills in ID, Name, Base URL, API Key, and Models
- **THEN** all values SHALL be captured and included in the save request
