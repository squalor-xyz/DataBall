# Row Builder Pattern Details

The row builder pattern in the `DataBall` class allows for safe and controlled addition of rows, with propagation of values from the previous row, handling of new columns, and application of configured relationships. Here's a detailed breakdown:

### Components:
- **_pendingRow**: A dictionary holding the values for the new row being built.
- **_originalRow**: A snapshot of the last row's values for comparison in relationships.
- **_modifiedFields**: A set tracking which fields have been explicitly modified to avoid resetting them in relationships.

### Methods:
1. **InitializeRow(Dictionary<string, object> initialValues = null)**:
   - Clears any existing pending state.
   - If there are existing rows, copies the last row's values into both `_originalRow` and `_pendingRow` for propagation.
   - Applies any provided `initialValues`, marking them as modified.
   - This sets up the base for the new row, inheriting unmodified fields from the previous one.

2. **ModifyField(string field, object value)**:
   - Checks if a pending row is initialized; throws exception if not.
   - Sets or updates the value in `_pendingRow`.
   - Adds the field to `_modifiedFields` to indicate explicit change.

3. **Roll()**:
   - Checks if a pending row exists; throws exception if not.
   - **Applies Relationships**:
     - For each configured relationship, compares the trigger field's value in `_pendingRow` and `_originalRow`.
     - If the trigger has changed (value differs, or presence changes), resets dependent fields (`reset` list) to null in `_pendingRow`, but only if they weren't explicitly modified.
   - **Adds New Columns**:
     - For any fields in `_pendingRow` not in the DataFrame, creates a new column with the expected type (from config) or inferred, filling previous rows with nulls.
   - **Prepares and Appends Row**:
     - Creates an array of values matching the current columns' order, using `_pendingRow` values or null.
     - Appends to the DataFrame.
   - Clears the pending state.

### Benefits:
- **Propagation**: Automatically carries over values, reducing redundancy in data entry for sequential measurements.
- **Safety**: Batch modifications before commit, with validation.
- **Flexibility**: Add new fields/columns dynamically.
- **Dependencies**: Relationships ensure data consistency by resetting dependents on key changes.
- **Integration with Config**: Uses expected types for new columns.

### Example Usage:
using System.Collections.Generic;

var db = new DataBall();
db.AddColumn<int>("Id", new[] { 1 });
db.AddColumn<string>("Name", new[] { "Test" });

db.InitializeRow();
db.ModifyField("Id", 2);
db.Roll();  // Adds row with propagated/modified values

If a relationship is configured (e.g., trigger "Id" resets "Name"), changing "Id" would set "Name" to null unless modified.