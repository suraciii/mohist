### Requirement: Delivery-Time Card Title Matches the Default Lens

The delivery-time scatter card's title MUST correspond to the metric caliber (lead time or cycle time) rendered by the card's default lens. A user glancing only at the card title MUST be able to tell which caliber the charted values represent without inspecting the lens toggle. The title MUST NOT name a caliber that contradicts the data the default lens plots.

#### Scenario: Default lens and title agree

- **WHEN** the delivery-time scatter card is rendered with its default lens
- **THEN** the card title MUST name the same caliber (lead time or cycle time) that the default lens plots

### Requirement: Lens Switch Updates the Displayed Caliber in the Title Area

When the user switches the delivery-time lens between lead time and cycle time, the card's title or subtitle MUST update so the newly selected caliber is readable from the title area alone. The title area MUST NOT keep describing a caliber that no longer matches the plotted data after a lens switch.

#### Scenario: Switching the lens reflects the new caliber in the title area

- **WHEN** the card is showing the default lens and the user toggles to the other lens
- **THEN** the card's title or subtitle MUST update to name the newly selected caliber
- **AND** the title area MUST NOT describe a caliber that contradicts the data plotted under the new lens

#### Scenario: Switching back restores the original caliber in the title area

- **WHEN** the user toggles the lens back to the original setting
- **THEN** the card's title or subtitle MUST again name the original caliber
