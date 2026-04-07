# Requirements Document

## Introduction

The multi-user-auth feature replaces the hardcoded credential check in ThatsMyQuant.Dashboard with a
proper multi-user authentication system backed by SQLite via Entity Framework Core. It introduces an
`OperatorUser` entity with role-based access control (Admin / Operator / ReadOnly), a secure
PBKDF2-SHA512 password hashing scheme, a first-run seed account, a session singleton, a
`ChangePasswordDialog` for forced password resets, and an Admin-only User Management tab in the
dashboard.

---

## Glossary

- **Dashboard**: The ThatsMyQuant.Dashboard WPF application.
- **QuantDbContext**: The Entity Framework Core `DbContext` that manages `quant.db`.
- **OperatorUser**: The entity representing a human operator who can log in to the Dashboard.
- **UserAuthService**: The application service responsible for all authentication and user-management operations.
- **IUserAuthService**: The interface contract for `UserAuthService`.
- **CurrentSession**: The application-lifetime singleton that holds the authenticated `OperatorUser` after a successful login.
- **LoginWindow**: The WPF window that collects credentials and initiates authentication.
- **ChangePasswordDialog**: The WPF dialog shown when `OperatorUser.MustChangePassword` is `true`.
- **UserManagementTab**: The Admin-only tab in the Dashboard that exposes user-management operations.
- **PasswordValidator**: The component that enforces the password complexity policy.
- **Role**: An enum with values `Admin`, `Operator`, and `ReadOnly` that controls feature access.
- **PBKDF2-SHA512**: The key-derivation function used to hash passwords (RFC 2898, SHA-512 PRF, ≥ 100 000 iterations, 16-byte random salt).

---

## Requirements

### Requirement 1: OperatorUser Entity

**User Story:** As a system architect, I want a well-defined `OperatorUser` entity stored in `quant.db`,
so that user identities and credentials are persisted securely.

#### Acceptance Criteria

1. THE `OperatorUser` entity SHALL have the following fields: `Id` (Guid, primary key), `Username`
   (string, max 64 chars, unique, required), `Email` (string, max 256 chars, unique, required),
   `PasswordHash` (string, required), `Role` (enum: Admin / Operator / ReadOnly, stored as string),
   `IsActive` (bool, required), `CreatedAt` (DateTime, required), `LastLoginAt` (DateTime, nullable),
   `MustChangePassword` (bool, required).
2. THE `QuantDbContext` SHALL expose a `DbSet<OperatorUser>` named `OperatorUsers`.
3. THE `QuantDbContext` SHALL configure a unique index on `OperatorUser.Username` and a separate unique
   index on `OperatorUser.Email`.
4. THE `QuantDbContext` SHALL store `OperatorUser.Role` as its string name (not integer ordinal) in the
   database column.

---

### Requirement 2: First-Run Seed

**User Story:** As a new installer, I want a default admin account created automatically on first run,
so that I can log in immediately without manual database setup.

#### Acceptance Criteria

1. WHEN the Dashboard starts AND no rows exist in the `OperatorUsers` table, THE `UserAuthService`
   SHALL create one `OperatorUser` with `Username = "admin"`, `Email = "admin@thatsmyquant.com"`,
   `Role = Admin`, `IsActive = true`, `MustChangePassword = true`, and a PBKDF2-SHA512 hash of the
   password `"Admin@123!"`.
2. WHEN at least one row already exists in the `OperatorUsers` table, THE `UserAuthService` SHALL NOT
   create any seed user.
3. THE `UserAuthService` SHALL apply any pending EF Core migrations before performing the seed check.

---

### Requirement 3: Password Hashing

**User Story:** As a security engineer, I want all passwords stored as PBKDF2-SHA512 hashes with a
per-user random salt, so that a database breach does not expose plaintext credentials.

#### Acceptance Criteria

1. WHEN a password is stored, THE `UserAuthService` SHALL hash it using PBKDF2 with SHA-512 as the PRF,
   a 16-byte cryptographically random salt, and a minimum of 100 000 iterations.
2. THE `UserAuthService` SHALL encode the salt and hash together in a single `PasswordHash` string using
   the format `{iterations}:{base64(salt)}:{base64(hash)}`.
3. WHEN verifying a password, THE `UserAuthService` SHALL extract the iteration count, salt, and hash
   from the stored `PasswordHash` string and recompute the hash for comparison using a constant-time
   equality check.
4. IF the stored `PasswordHash` string does not conform to the expected format, THEN THE
   `UserAuthService` SHALL return an authentication failure without throwing an unhandled exception.

---

### Requirement 4: Authentication

**User Story:** As an operator, I want to log in with my email and password, so that I can access the
Dashboard securely.

#### Acceptance Criteria

1. WHEN `AuthenticateAsync(email, password)` is called with a matching email and correct password for an
   active user, THE `UserAuthService` SHALL return the corresponding `OperatorUser` and update
   `LastLoginAt` to the current UTC time.
2. WHEN `AuthenticateAsync` is called with an email that does not exist in `OperatorUsers`, THE
   `UserAuthService` SHALL return `null`.
3. WHEN `AuthenticateAsync` is called with a correct email but an incorrect password, THE
   `UserAuthService` SHALL return `null`.
4. WHEN `AuthenticateAsync` is called for a user whose `IsActive` is `false`, THE `UserAuthService`
   SHALL return `null`.
5. THE `LoginWindow` SHALL call `UserAuthService.AuthenticateAsync` instead of performing a hardcoded
   credential comparison.
6. WHEN `AuthenticateAsync` returns `null`, THE `LoginWindow` SHALL display the message
   `"Invalid credentials or account is inactive."` and SHALL NOT close.
7. WHEN `AuthenticateAsync` returns a valid `OperatorUser`, THE `LoginWindow` SHALL store the user in
   `CurrentSession` and close with `DialogResult = true`.

---

### Requirement 5: Forced Password Change

**User Story:** As a security administrator, I want users with `MustChangePassword = true` to be forced
to set a new password immediately after login, so that default or reset passwords are never used beyond
the first session.

#### Acceptance Criteria

1. WHEN a user successfully authenticates AND `OperatorUser.MustChangePassword` is `true`, THE
   `LoginWindow` SHALL open the `ChangePasswordDialog` before navigating to the main window.
2. WHEN the `ChangePasswordDialog` is submitted with a new password that satisfies the password policy,
   THE `UserAuthService` SHALL call `UpdatePasswordAsync` to persist the new hash and set
   `MustChangePassword = false`.
3. WHEN the `ChangePasswordDialog` is cancelled or closed without submitting, THE `LoginWindow` SHALL
   log the user out and return to the login screen.
4. IF the new password entered in `ChangePasswordDialog` does not satisfy the password policy, THEN THE
   `ChangePasswordDialog` SHALL display a descriptive validation error and SHALL NOT submit.

---

### Requirement 6: Session Singleton

**User Story:** As a developer, I want a `CurrentSession` singleton that holds the authenticated user
for the lifetime of the application, so that any component can query the current user and role without
re-authenticating.

#### Acceptance Criteria

1. THE `CurrentSession` SHALL expose a static property `Current` of type `OperatorUser?` that is `null`
   before login and non-null after a successful login.
2. WHEN a user logs in successfully, THE `LoginWindow` SHALL set `CurrentSession.Current` to the
   authenticated `OperatorUser`.
3. THE `CurrentSession` SHALL expose a static method `Clear()` that sets `Current` to `null`, for use
   on logout.
4. WHILE `CurrentSession.Current` is `null`, THE Dashboard main window SHALL NOT be accessible.

---

### Requirement 7: Role-Based UI Enforcement

**User Story:** As a product owner, I want the Dashboard UI to reflect each user's role, so that
ReadOnly users cannot execute trades and only Admins can manage users.

#### Acceptance Criteria

1. WHILE `CurrentSession.Current.Role` is `ReadOnly`, THE Dashboard SHALL disable all trade-execution
   buttons (Buy, Sell, and any manual order controls).
2. WHILE `CurrentSession.Current.Role` is `Operator` or `Admin`, THE Dashboard SHALL enable all
   trade-execution buttons.
3. WHILE `CurrentSession.Current.Role` is `Admin`, THE Dashboard SHALL display the `UserManagementTab`.
4. WHILE `CurrentSession.Current.Role` is `Operator` or `ReadOnly`, THE Dashboard SHALL hide the
   `UserManagementTab`.

---

### Requirement 8: IUserAuthService Interface

**User Story:** As a developer, I want a well-defined service interface for all auth operations, so that
the UI layer depends on an abstraction and the implementation can be tested or swapped independently.

#### Acceptance Criteria

1. THE `IUserAuthService` interface SHALL declare the following methods:
   - `Task<OperatorUser?> AuthenticateAsync(string email, string password)`
   - `Task<OperatorUser> CreateUserAsync(string username, string email, string password, Role role)`
   - `Task UpdatePasswordAsync(Guid userId, string newPassword)`
   - `Task<IReadOnlyList<OperatorUser>> ListUsersAsync()`
   - `Task SetUserActiveAsync(Guid userId, bool isActive)`
   - `Task DeleteUserAsync(Guid userId)`
2. THE `UserAuthService` class SHALL implement `IUserAuthService`.
3. THE `UserAuthService` SHALL be registered in the Dashboard's DI container as a scoped or singleton
   service bound to `IUserAuthService`.

---

### Requirement 9: User Management Tab

**User Story:** As an Admin, I want a User Management tab in the Dashboard where I can create, view,
reset, deactivate, and delete operator accounts, so that I can control who has access to the system.

#### Acceptance Criteria

1. THE `UserManagementTab` SHALL display a list of all `OperatorUser` records showing `Username`,
   `Email`, `Role`, `IsActive`, `CreatedAt`, and `LastLoginAt`.
2. WHEN an Admin clicks "Add User", THE `UserManagementTab` SHALL open a dialog to collect `Username`,
   `Email`, `Password`, and `Role`, then call `IUserAuthService.CreateUserAsync`.
3. WHEN an Admin clicks "Reset Password" for a selected user, THE `UserManagementTab` SHALL open a
   dialog to collect a new password, then call `IUserAuthService.UpdatePasswordAsync` and set
   `MustChangePassword = true` for that user.
4. WHEN an Admin clicks "Toggle Active" for a selected user, THE `UserManagementTab` SHALL call
   `IUserAuthService.SetUserActiveAsync` with the toggled value.
5. WHEN an Admin clicks "Delete" for a selected user, THE `UserManagementTab` SHALL display a
   confirmation prompt and, upon confirmation, call `IUserAuthService.DeleteUserAsync`.
6. IF an Admin attempts to delete or deactivate the last remaining active Admin account, THEN THE
   `UserManagementTab` SHALL display an error message and SHALL NOT perform the operation.
7. WHEN any user-management operation completes successfully, THE `UserManagementTab` SHALL refresh the
   user list.

---

### Requirement 10: Password Policy

**User Story:** As a security administrator, I want all passwords to meet a minimum complexity policy,
so that weak passwords cannot be set on any account.

#### Acceptance Criteria

1. THE `PasswordValidator` SHALL reject any password shorter than 8 characters.
2. THE `PasswordValidator` SHALL reject any password that does not contain at least one uppercase
   ASCII letter (A–Z).
3. THE `PasswordValidator` SHALL reject any password that does not contain at least one ASCII decimal
   digit (0–9).
4. THE `PasswordValidator` SHALL reject any password that does not contain at least one special
   character from the set: `! @ # $ % ^ & * ( ) _ + - = [ ] { } ; ' : " , . < > ? /`.
5. WHEN a password fails validation, THE `PasswordValidator` SHALL return a descriptive error message
   identifying which rule was violated.
6. THE `PasswordValidator` SHALL be used by `UserAuthService.CreateUserAsync` and
   `UserAuthService.UpdatePasswordAsync` before hashing any password.
7. IF a password supplied to `CreateUserAsync` or `UpdatePasswordAsync` fails validation, THEN THE
   `UserAuthService` SHALL throw an `ArgumentException` containing the validation error message and
   SHALL NOT persist any changes.
