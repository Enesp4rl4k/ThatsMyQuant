# Implementation Plan: multi-user-auth

## Overview

Replace the hardcoded credential check in `ThatsMyQuant.Dashboard` with a full multi-user
authentication system. Tasks are ordered so each step compiles and integrates cleanly into the
previous one. All code is C# / WPF targeting .NET 9.

---

## Tasks

- [x] 1. Add `OperatorUser` entity and `Role` enum to `06_Contracts`
  - [x] 1.1 Create `06_Contracts/Models/OperatorUser.cs`
    - Define `public enum Role { Admin, Operator, ReadOnly }` in the same file or a sibling file
    - Define `OperatorUser` class with all nine fields: `Id` (Guid, default `Guid.NewGuid()`),
      `Username` (string, max 64), `Email` (string, max 256), `PasswordHash` (string),
      `Role` (Role enum), `IsActive` (bool, default `true`), `CreatedAt` (DateTime, default
      `DateTime.UtcNow`), `LastLoginAt` (DateTime?), `MustChangePassword` (bool)
    - Namespace: `ThatsMyQuant.Contracts.Models`
    - _Requirements: 1.1_

- [x] 2. Add `IUserAuthService` interface to `06_Contracts`
  - [x] 2.1 Create `06_Contracts/Interfaces/IUserAuthService.cs`
    - Declare all six methods exactly as specified in the design:
      `AuthenticateAsync`, `CreateUserAsync`, `UpdatePasswordAsync`,
      `ListUsersAsync`, `SetUserActiveAsync`, `DeleteUserAsync`
    - Namespace: `ThatsMyQuant.Contracts.Interfaces`
    - Reference types: `OperatorUser`, `Role` from `ThatsMyQuant.Contracts.Models`
    - _Requirements: 8.1_

- [x] 3. Extend `QuantDbContext` with `OperatorUser` EF Core configuration
  - [x] 3.1 Add `DbSet<OperatorUser>` property to `QuantDbContext`
    - File: `03_Infrastructure/Data.Persistence/QuantDbContext.cs`
    - Add `public DbSet<OperatorUser> OperatorUsers => Set<OperatorUser>();` alongside the
      existing `DbSet` properties (after line 14)
    - _Requirements: 1.2_
  - [x] 3.2 Add `OperatorUser` entity configuration inside `OnModelCreating`
    - File: `03_Infrastructure/Data.Persistence/QuantDbContext.cs`
    - Append `modelBuilder.Entity<OperatorUser>(entity => { ... })` block after the
      `WebhookRequestLog` configuration (after line ~115)
    - Configure: `ToTable("OperatorUsers")`, `HasKey(u => u.Id)`,
      `Username` required + `HasMaxLength(64)`, `Email` required + `HasMaxLength(256)`,
      `PasswordHash` required, `Role` with `HasConversion<string>()` + `HasMaxLength(16)`,
      `IsActive` required, `CreatedAt` required, `MustChangePassword` required,
      unique index on `Username`, unique index on `Email`
    - _Requirements: 1.2, 1.3, 1.4_

- [x] 4. Implement `PasswordHasher` static class
  - [x] 4.1 Create `04_UI/ThatsMyQuant.Dashboard/Auth/PasswordHasher.cs`
    - Namespace: `ThatsMyQuant.Dashboard.Auth`
    - Constants: `SaltBytes = 16`, `HashBytes = 64`, `Iterations = 100_000`
    - `Hash(string password) → string`: generate 16-byte `RandomNumberGenerator` salt,
      derive 64-byte key via `Rfc2898DeriveBytes` (SHA-512 PRF, 100 000 iterations),
      return `$"{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}"`
    - `Verify(string password, string storedHash) → bool`: split on `:`, expect 3 parts,
      parse iterations, decode salt and expected hash from Base64, recompute PBKDF2,
      compare with `CryptographicOperations.FixedTimeEquals`; return `false` (not throw)
      on any malformed input
    - _Requirements: 3.1, 3.2, 3.3, 3.4_

  - [x] 4.2 Write property test P2: hash format is always well-formed
    - File: `05_Tests/ThatsMyQuant.Tests/Auth/PasswordHasherTests.cs`
    - Add FsCheck NuGet reference to test project (`FsCheck.Xunit`)
    - Generate random non-empty strings; assert result matches
      `^\d+:[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+$` with exactly 2 `:` separators,
      first segment ≥ 100 000, second segment decodes to 16 bytes, third to 64 bytes
    - Tag: `// Feature: multi-user-auth, Property 2: hash format is always well-formed`
    - _Requirements: 3.1, 3.2_

  - [x] 4.3 Write property test P3: hash round-trip
    - File: `05_Tests/ThatsMyQuant.Tests/Auth/PasswordHasherTests.cs`
    - Generate random password strings; assert `Verify(p, Hash(p)) == true`
    - Tag: `// Feature: multi-user-auth, Property 3: password hash round-trip`
    - _Requirements: 3.3_

  - [x] 4.4 Write property test P4: malformed hash never throws
    - File: `05_Tests/ThatsMyQuant.Tests/Auth/PasswordHasherTests.cs`
    - Generate arbitrary strings (empty, single-segment, two-segment, non-numeric iteration,
      invalid Base64); assert `Verify` returns `false` without throwing
    - Tag: `// Feature: multi-user-auth, Property 4: malformed hash never throws`
    - _Requirements: 3.4_

- [x] 5. Implement `PasswordValidator` static class
  - [x] 5.1 Create `04_UI/ThatsMyQuant.Dashboard/Auth/PasswordValidator.cs`
    - Namespace: `ThatsMyQuant.Dashboard.Auth`
    - `Validate(string password) → string?`: check rules in order:
      1. length ≥ 8 → `"Password must be at least 8 characters."`
      2. contains `[A-Z]` → `"Password must contain at least one uppercase letter."`
      3. contains `[0-9]` → `"Password must contain at least one digit."`
      4. contains one of `! @ # $ % ^ & * ( ) _ + - = [ ] { } ; ' : " , . < > ? /`
         → `"Password must contain at least one special character."`
      Return `null` if all rules pass
    - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5_

  - [x] 5.2 Write property test P12: validator rejects non-compliant and accepts compliant passwords
    - File: `05_Tests/ThatsMyQuant.Tests/Auth/PasswordValidatorTests.cs`
    - Generate strings violating each rule individually; assert `Validate` returns non-null,
      non-empty string
    - Generate strings satisfying all four rules; assert `Validate` returns `null`
    - Tag: `// Feature: multi-user-auth, Property 12: PasswordValidator rejects non-compliant passwords`
    - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5_

- [x] 6. Implement `UserAuthService`
  - [x] 6.1 Create `04_UI/ThatsMyQuant.Dashboard/Auth/UserAuthService.cs`
    - Namespace: `ThatsMyQuant.Dashboard.Auth`
    - Constructor: `UserAuthService(QuantDbContext db)`
    - Implement `IUserAuthService` (reference `ThatsMyQuant.Contracts.Interfaces`)
    - `SeedAsync()`: call `db.Database.EnsureCreatedAsync()`, then if
      `!await db.OperatorUsers.AnyAsync()` insert the default admin
      (`Username="admin"`, `Email="admin@thatsmyquant.com"`, `Role=Admin`,
      `IsActive=true`, `MustChangePassword=true`, hash of `"Admin@123!"`)
    - `AuthenticateAsync(email, password)`: case-insensitive email lookup,
      return `null` if not found / inactive / wrong password;
      on success set `LastLoginAt = DateTime.UtcNow`, save, return user
    - `CreateUserAsync(username, email, password, role)`: call
      `PasswordValidator.Validate` → throw `ArgumentException` on failure;
      hash password, create entity with `MustChangePassword=false`, `IsActive=true`, save
    - `UpdatePasswordAsync(userId, newPassword)`: validate → throw on failure;
      hash, set `MustChangePassword=false`, save
    - `ListUsersAsync()`: return `await db.OperatorUsers.ToListAsync()`
    - `SetUserActiveAsync(userId, isActive)`: if `isActive==false`, guard that at least
      one other active Admin exists → throw `InvalidOperationException` if not; save
    - `DeleteUserAsync(userId)`: guard last active Admin → throw `InvalidOperationException`;
      remove and save
    - _Requirements: 2.1, 2.2, 2.3, 3.1, 3.2, 3.3, 4.1, 4.2, 4.3, 4.4, 8.2, 9.2, 9.3,
      9.4, 9.5, 9.6, 10.6, 10.7_

  - [x] 6.2 Write property test P5: authentication succeeds only for active users with correct credentials
    - File: `05_Tests/ThatsMyQuant.Tests/Auth/UserAuthServiceTests.cs`
    - Use in-memory SQLite (`options.UseSqlite("DataSource=:memory:")`) for each test
    - Generate random email/password combinations not matching any seeded user;
      assert `AuthenticateAsync` returns `null`
    - Assert inactive user returns `null` regardless of password
    - Assert correct credentials for active user return the user and update `LastLoginAt`
    - Tag: `// Feature: multi-user-auth, Property 5: authentication succeeds only for active users with correct credentials`
    - _Requirements: 4.1, 4.2, 4.3, 4.4_

  - [x] 6.3 Write property test P8: seed is idempotent
    - File: `05_Tests/ThatsMyQuant.Tests/Auth/UserAuthServiceTests.cs`
    - Pre-populate DB with at least one user; call `SeedAsync()` N times (N ≥ 2);
      assert `OperatorUsers.Count()` is unchanged after each call
    - Tag: `// Feature: multi-user-auth, Property 8: seed is idempotent`
    - _Requirements: 2.2_

  - [x] 6.4 Write property test P13: service throws on invalid password
    - File: `05_Tests/ThatsMyQuant.Tests/Auth/UserAuthServiceTests.cs`
    - Generate passwords that `PasswordValidator.Validate` rejects; assert both
      `CreateUserAsync` and `UpdatePasswordAsync` throw `ArgumentException` containing
      the validation message, and no DB row is inserted or updated
    - Tag: `// Feature: multi-user-auth, Property 13: service methods throw on invalid password`
    - _Requirements: 10.6, 10.7_

  - [x] 6.5 Write unit tests for `UserAuthService`
    - File: `05_Tests/ThatsMyQuant.Tests/Auth/UserAuthServiceTests.cs`
    - `SeedAsync` on empty DB creates exactly one admin with `Username="admin"` and
      `MustChangePassword=true`
    - `AuthenticateAsync` with seeded admin credentials returns the user
    - `CreateUserAsync` with valid password creates a user retrievable via `ListUsersAsync`
    - `SetUserActiveAsync` and `DeleteUserAsync` throw `InvalidOperationException` when
      targeting the last active Admin
    - _Requirements: 2.1, 4.1, 9.4, 9.5, 9.6_

- [x] 7. Implement `CurrentSession` singleton
  - [x] 7.1 Create `04_UI/ThatsMyQuant.Dashboard/Auth/CurrentSession.cs`
    - Namespace: `ThatsMyQuant.Dashboard.Auth`
    - `private static volatile OperatorUser? _current;`
    - `public static OperatorUser? Current { get => _current; internal set => _current = value; }`
    - `public static void Clear() => _current = null;`
    - _Requirements: 6.1, 6.3_

  - [x] 7.2 Write unit tests for `CurrentSession`
    - File: `05_Tests/ThatsMyQuant.Tests/Auth/CurrentSessionTests.cs`
    - Set `Current` to a user, assert non-null; call `Clear()`, assert `null`
    - Verify `Current` is `null` before any assignment
    - _Requirements: 6.1, 6.3_

- [x] 8. Checkpoint — ensure solution builds and existing tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 9. Update `LoginWindow` to use `UserAuthService` asynchronously
  - [x] 9.1 Modify `04_UI/ThatsMyQuant.Dashboard/LoginWindow.xaml.cs`
    - Change `Login_Click` signature to `private async void Login_Click(...)`
    - Resolve `IUserAuthService` from `App.ServiceProvider`
    - Replace hardcoded credential check (lines 27–36) with:
      1. `var user = await authService.AuthenticateAsync(EmailBox.Text, PasswordBox.Password)`
      2. If `null` → set `ErrorText.Text = "Invalid credentials or account is inactive."`,
         show `ErrorText`, return
      3. Set `CurrentSession.Current = user`
      4. If `user.MustChangePassword` → open `ChangePasswordDialog`; if cancelled →
         `CurrentSession.Clear()`, return
      5. `DialogResult = true; Close()`
    - _Requirements: 4.5, 4.6, 4.7, 5.1, 5.3, 6.2_

- [x] 10. Implement `ChangePasswordDialog`
  - [x] 10.1 Create `04_UI/ThatsMyQuant.Dashboard/Views/ChangePasswordDialog.xaml`
    - Two `PasswordBox` controls bound to `NewPassword` and `ConfirmPassword`
    - An error `TextBlock` bound to `ErrorMessage` (collapsed when empty)
    - Submit button bound to `SubmitCommand`; Cancel button bound to `CancelCommand`
    - _Requirements: 5.2, 5.3, 5.4_
  - [x] 10.2 Create `04_UI/ThatsMyQuant.Dashboard/Views/ChangePasswordDialog.xaml.cs`
    - Code-behind wires `PasswordBox.PasswordChanged` to ViewModel properties
      (PasswordBox cannot use standard binding)
    - On `SubmitCommand` success: `DialogResult = true; Close()`
    - On `CancelCommand`: `DialogResult = false; Close()`
    - _Requirements: 5.2, 5.3_
  - [x] 10.3 Create `ChangePasswordDialogViewModel` (inline in the code-behind file or a
    sibling `ChangePasswordDialogViewModel.cs` in `ViewModels/SubViewModels/`)
    - Properties: `NewPassword`, `ConfirmPassword`, `ErrorMessage` (all `[ObservableProperty]`)
    - `SubmitCommand`: validate via `PasswordValidator.Validate`, check passwords match,
      call `IUserAuthService.UpdatePasswordAsync(CurrentSession.Current!.Id, NewPassword)`,
      set error message on failure
    - `CancelCommand`: close dialog
    - _Requirements: 5.2, 5.4_

- [x] 11. Implement `UserManagementViewModel`
  - [x] 11.1 Create `04_UI/ThatsMyQuant.Dashboard/ViewModels/SubViewModels/UserManagementViewModel.cs`
    - Namespace: `ThatsMyQuant.Dashboard.ViewModels.SubViewModels`
    - Constructor: `UserManagementViewModel(IUserAuthService authService)`
    - `[ObservableProperty] ObservableCollection<OperatorUser> _users`
    - `[ObservableProperty] OperatorUser? _selectedUser`
    - `LoadUsersCommand` (`IAsyncRelayCommand`): populate `Users` from `ListUsersAsync()`
    - `AddUserCommand`: open an inline dialog to collect username/email/password/role,
      call `CreateUserAsync`, then `LoadUsersCommand`
    - `ResetPasswordCommand`: open dialog for new password, call `UpdatePasswordAsync`,
      set `MustChangePassword=true` via a direct DB update or a dedicated service call,
      then `LoadUsersCommand`
    - `ToggleActiveCommand`: call `SetUserActiveAsync(!SelectedUser.IsActive)`,
      catch `InvalidOperationException` and show error, then `LoadUsersCommand`
    - `DeleteUserCommand`: show confirmation `MessageBox`, on confirm call `DeleteUserAsync`,
      catch `InvalidOperationException` and show error, then `LoadUsersCommand`
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7_

- [x] 12. Implement `UserManagementTab` XAML
  - [x] 12.1 Create `04_UI/ThatsMyQuant.Dashboard/Views/UserManagementTab.xaml`
    - `DataGrid` with `AutoGenerateColumns="False"`, `IsReadOnly="True"`,
      `ItemsSource="{Binding Users}"`, `SelectedItem="{Binding SelectedUser}"`
    - Columns: Username, Email, Role, Active (CheckBox), CreatedAt
      (`StringFormat=yyyy-MM-dd`), LastLoginAt (`StringFormat=yyyy-MM-dd HH:mm`)
    - Toolbar `StackPanel` with buttons: "Add User" → `AddUserCommand`,
      "Reset Password" → `ResetPasswordCommand`, "Toggle Active" → `ToggleActiveCommand`,
      "Delete" → `DeleteUserCommand`
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5_
  - [x] 12.2 Add `UserManagementTab` to `MainWindow.xaml`
    - Add a `<TabItem>` inside the existing `TabControl` with
      `Header="User Management"` and
      `Visibility="{Binding IsAdminUser, Converter={StaticResource BoolToVisibilityConverter}}"`
    - Set `DataContext` of the tab content to `UserManagementViewModel`
    - _Requirements: 7.3, 7.4_

- [x] 13. Add role-based properties to `MainViewModel`
  - [x] 13.1 Modify `04_UI/ThatsMyQuant.Dashboard/ViewModels/MainViewModel.cs`
    - Add `public bool CanExecuteTrades => CurrentSession.Current?.Role is Role.Operator or Role.Admin;`
    - Add `public bool IsAdminUser => CurrentSession.Current?.Role == Role.Admin;`
    - Bind `IsEnabled="{Binding CanExecuteTrades}"` on all trade-execution buttons
      (Buy, Sell, manual order controls) in `MainWindow.xaml`
    - _Requirements: 7.1, 7.2, 7.3, 7.4_

- [x] 14. Register services in DI and call `SeedAsync` at startup
  - [x] 14.1 Modify `04_UI/ThatsMyQuant.Dashboard/App.xaml.cs`
    - Add `using ThatsMyQuant.Dashboard.Auth;` and
      `using ThatsMyQuant.Contracts.Interfaces;`
    - In `ConfigureServices`: register
      `services.AddScoped<IUserAuthService, UserAuthService>();`
    - In `EnsureDatabaseReadyAsync`: after `db.Database.EnsureCreatedAsync()`, resolve
      `IUserAuthService` from the scope and call `await authService.SeedAsync()`
    - _Requirements: 2.3, 8.3_

- [x] 15. Add FsCheck to test project and wire up Auth test files
  - [x] 15.1 Add `FsCheck.Xunit` NuGet package to
    `05_Tests/ThatsMyQuant.Tests/ThatsMyQuant.Tests.csproj`
    - Add `<PackageReference Include="FsCheck.Xunit" Version="3.*" />`
    - Add `<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.*" />`
      (for in-memory SQLite in `UserAuthServiceTests`)
    - Add project reference to `ThatsMyQuant.Dashboard` if not already present, or
      reference the `Auth/` classes directly via a shared assembly reference
    - _Requirements: (test infrastructure)_

- [x] 16. Final checkpoint — build and test
  - Ensure all tests pass, ask the user if questions arise.

---

## Notes

- Tasks marked with `*` are optional and can be skipped for a faster MVP
- Each task references specific requirements for traceability
- Property tests use FsCheck with a minimum of 100 iterations per property
- Unit tests use xUnit + FluentAssertions (already in the test project)
- In-memory SQLite (`DataSource=:memory:`) is used for `UserAuthService` tests to avoid
  touching the real `quant.db`
- `PasswordBox` in WPF cannot use standard `{Binding}` — code-behind must forward the
  `Password` property to the ViewModel on `PasswordChanged`
- The `EnsureCreated` migration strategy is retained (no `dotnet ef migrations add` needed)
