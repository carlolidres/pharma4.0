# Pharma 4.0 - ASP.NET Core Launch Pad

This project has been converted from the PHP/XAMPP prototype into an ASP.NET Core Razor Pages application.

## Entry Points

- `/Auth` - sliding sign-in/sign-up page
- `/Dashboard` - authenticated Pharma 4.0 Launch Pad
- `/Logout` - clears the active session
- `/Api/Tcodes?handler=List` - transaction directory JSON endpoint
- `/Api/Tcodes?handler=Launch&tcode=DASH` - transaction launch JSON endpoint

## Run Locally

```powershell
dotnet run --project "C:\Users\Carlo Mauring Lidres\OneDrive\Desktop\Projects\C# Project\Pharma4.0\Pharma40.csproj" --urls http://localhost:5088
```

Then open:

```text
http://localhost:5088/
```

## Notes

- Passwords are stored with PBKDF2 salted hashing.
- Sessions protect the Launch Pad.
- Local app data is created automatically in `App_Data/app-data.json`.
- The first registered user becomes `Administrator`; later users become `User`.
- Theme choices are available in the profile menu and saved in browser `localStorage`.

## Deployment SMTP Setup

## JWT Authentication Setup

API authentication uses JWT Bearer tokens. Local placeholders are in `appsettings.Development.json`; production must use environment variables or secrets.

```powershell
JWT__Issuer=Pharma40
JWT__Audience=Pharma40Users
JWT__SecretKey=CHANGE_THIS_TO_A_LONG_SECURE_SECRET_KEY_32_CHARS_MINIMUM
JWT__AccessTokenExpirationMinutes=60
JWT__RefreshTokenExpirationDays=7
JWT__RefreshTokenTTL=2
```

Login returns a bearer token from `POST /api/auth/login` and the Razor login page stores it in `localStorage` for current frontend API calls. TODO: replace localStorage token persistence with an httpOnly refresh-token cookie before internet-facing production deployment.

Email sending is selected by `EmailProvider:Provider`.

Supported values:

- `Smtp`
- `GmailApi`

Set with:

```powershell
EMAILPROVIDER__PROVIDER=GmailApi
```

### Gmail API Setup

Gmail API sending uses OAuth 2.0 with the `https://www.googleapis.com/auth/gmail.send` scope. An API key alone cannot send email.

Google Cloud setup:

1. Open Google Cloud Console.
2. Select project `gmailsmtp-496812` (`GmailSMTP`, project number `512872766549`).
3. Enable the Gmail API.
4. Configure the OAuth consent screen.
5. Create an OAuth Client ID.
6. Use `Desktop app` for local development, or `Web application` for production if you are implementing a server-hosted OAuth callback.
7. Download the OAuth client credentials JSON file.
8. Save it locally as `Config/google-oauth-client.json`.
9. Do not commit the credentials JSON file.
10. On first use, authorize the Gmail account that will send operational emails.
11. The generated token is stored in `Config/gmail-token-store`.
12. For production, provide OAuth credentials and token store through secure hosting secrets or mounted secure files.

Gmail API configuration:

```powershell
EMAILPROVIDER__PROVIDER=GmailApi
GOOGLEGMAILAPI__APPLICATIONNAME=Pharma 4.0 Validation Management
GOOGLEGMAILAPI__PROJECTID=gmailsmtp-496812
GOOGLEGMAILAPI__SENDEREMAIL=your-gmail-address@gmail.com
GOOGLEGMAILAPI__CREDENTIALSPATH=Config/google-oauth-client.json
GOOGLEGMAILAPI__TOKENSTOREPATH=Config/gmail-token-store
GOOGLEGMAILAPI__TIMEOUTSECONDS=30
```

The following files are ignored by Git:

```text
Config/google-oauth-client.json
Config/gmail-token-store/
*.credentials.json
*.token.json
```

### SMTP Setup

SMTP settings are read from `Smtp` in `appsettings.Development.json` or `appsettings.Production.json`. Do not commit real SMTP passwords. In production, set environment variables so secrets override configuration files:

```powershell
SMTP__Provider=Gmail
SMTP__Host=smtp.gmail.com
SMTP__Port=587
SMTP__EnableSsl=true
SMTP__SenderName=Pharma 4.0 Validation Management
SMTP__SenderEmail=your-email@gmail.com
SMTP__Username=your-email@gmail.com
SMTP__Password=your-app-password
```

For Outlook / Microsoft 365:

```powershell
SMTP__Provider=Outlook
SMTP__Host=smtp.office365.com
SMTP__Port=587
SMTP__EnableSsl=true
SMTP__SenderEmail=your-email@domain.com
SMTP__Username=your-email@domain.com
SMTP__Password=your-password-or-app-secret
```

Gmail requires an App Password. Microsoft 365 may require SMTP AUTH to be enabled for the mailbox or tenant.

Gmail:
Use an App Password, not your normal Gmail password.

Outlook/Microsoft 365:
Use `smtp.office365.com`, port `587`, STARTTLS. If authentication fails, check whether SMTP AUTH is enabled for the mailbox/tenant.

Implemented email-backed flows include email verification OTP, forgot-password OTP, temporary password delivery, forced password change, password change confirmation, VRMS routing notifications, VRMS follow-up reminders, email logs, in-app notifications, and audit trail records. Admins can test SMTP with `POST /api/admin/email/test-smtp`.

## Offline Password Reset and Authenticator Verification

Forgot Password is configured for local/offline operation. Users are instructed to contact a system administrator instead of relying on email OTP delivery. Administrators can reset an account from USER management or call `POST /api/accounts/{id}/reset-password`; the system applies the approved temporary password, forces password change at next login, and records an audit action named `Password Reset`. The temporary password value is not written to logs or audit records.

TOTP authenticator verification is available as the preferred non-email verification mechanism. Authenticated users can start setup with `POST /api/auth/totp/setup`, then verify the first 6-digit code with `POST /api/auth/totp/verify-setup`. The app stores the TOTP secret protected with ASP.NET Core Data Protection and does not return it again after setup. Administrators can recover a locked-out user by calling `POST /api/accounts/{id}/totp/reset`.
