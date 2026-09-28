using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Server.Tools;

[McpServerToolType]
public sealed class ContactTools
{
    [McpServerTool(ReadOnly = true)]
    [Description("Get contacts sorted by most recently contacted")]
    public static async Task<string> GetContacts(
        [Description("The ID of the contact folder")]
        string folderId,
        [Description("The search query to filter contacts by")]
        string searchQuery,
        [Description("The maximum number of results to return")]
        int maxResults,
        [Description("The index to start from")]
        int startIndex,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            var response = await userContext.GetAsync<JsonElement>($"api/v1/contacts/address-book/{sourceIdPath}/");

            var simplifiedContacts = new List<object>();
            if (response.TryGetProperty("success", out var success) && success.GetBoolean() &&
                response.TryGetProperty("contacts", out var contacts))
                foreach (var contact in contacts.EnumerateArray())
                {
                    // Check if this is a contact group (contactType == 2)
                    var contactType = contact.TryGetProperty("contactType", out var ct) ? ct.GetInt32() : 0;
                    var isGroup = contactType == 2;

                    string? emailList = null;
                    int? memberCount = null;

                    if (isGroup)
                    {
                        // For groups, get member count from groupedContacts
                        if (contact.TryGetProperty("groupedContacts", out var groupedContacts))
                            memberCount = groupedContacts.GetArrayLength();
                    }
                    else
                    {
                        // For individual contacts, get email from emailAddressList
                        if (contact.TryGetProperty("emailAddressList", out var emails) && emails.GetArrayLength() > 0)
                        {
                            var firstEmail = emails[0];
                            // Handle both string and object formats
                            if (firstEmail.ValueKind == JsonValueKind.String)
                                emailList = firstEmail.GetString();
                            else if (firstEmail.ValueKind == JsonValueKind.Object &&
                                     firstEmail.TryGetProperty("address", out var addr))
                                emailList = addr.GetString();
                        }
                    }

                    var simplifiedContact = new
                    {
                        id = contact.TryGetProperty("id", out var id)
                            ? (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString())
                            : null,
                        username = contact.TryGetProperty("username", out var user) ? user.GetString() : null,
                        displayAs = (contact.TryGetProperty("displayAs", out var display) ? display.GetString() : null) ??
                                    (contact.TryGetProperty("DisplayAs", out var d2) ? d2.GetString() : null) ??
                                    (contact.TryGetProperty("displayName", out var dn) ? dn.GetString() : null) ??
                                    (contact.TryGetProperty("DisplayName", out var dn2) ? dn2.GetString() : null) ??
                                    (contact.TryGetProperty("fullName", out var fn) ? fn.GetString() : null) ??
                                    (contact.TryGetProperty("FullName", out var fn2) ? fn2.GetString() : null),
                        emailAddress = emailList ??
                                       (contact.TryGetProperty("email", out var em) ? em.GetString() : null) ??
                                       (contact.TryGetProperty("Email", out var em2) ? em2.GetString() : null) ??
                                       (contact.TryGetProperty("emailAddress", out var ea) ? ea.GetString() : null) ??
                                       (contact.TryGetProperty("EmailAddress", out var ea2) ? ea2.GetString() : null),
                        isGroup,
                        memberCount
                    };
                    simplifiedContacts.Add(simplifiedContact);
                }

            var result = new
            {
                contacts = simplifiedContacts,
                success = response.TryGetProperty("success", out success) && success.GetBoolean(),
                resultCode = response.TryGetProperty("resultCode", out var resultCode)
                    ? (resultCode.ValueKind == JsonValueKind.Number ? resultCode.GetInt32().ToString() : resultCode.GetString())
                    : null
            };

            return JsonSerializer.Serialize(result);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Get full details for a specific contact by ID and source ID")]
    public static async Task<string> GetContact(
        [Description("The ID of the contact folder")]
        string folderId,
        [Description("The ID of the contact to retrieve")]
        string contactId,
        UserContext userContext)
    {
        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            var response = await userContext.GetAsync<JsonElement>($"api/v1/contacts/get/{sourceIdPath}/{contactId}/");

            if (response.TryGetProperty("success", out var success) && success.GetBoolean() &&
                response.TryGetProperty("contact", out var contact))
            {
                // Check if this is a contact group (contactType == 2)
                var contactType = contact.TryGetProperty("contactType", out var ct) ? ct.GetInt32() : 0;
                var isGroup = contactType == 2;

                // Build enhanced response with group info
                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["isGroup"] = isGroup,
                    ["contactType"] = contactType
                };

                // Copy all contact properties
                foreach (var prop in contact.EnumerateObject())
                {
                    result[prop.Name] = prop.Value;
                }

                // Add member count for groups
                if (isGroup && contact.TryGetProperty("groupedContacts", out var groupedContacts))
                {
                    result["memberCount"] = groupedContacts.GetArrayLength();
                }

                return JsonSerializer.Serialize(result);
            }

            return JsonSerializer.Serialize(new { success = false, error = $"Contact not found for ID '{contactId}' in folder '{folderId}'. Use get_contacts to find valid contact IDs." });
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Get contacts from the global address book")]
    public static async Task<string> GetGlobalAddressBook(
        UserContext userContext)
    {
        try
        {
            var response = await userContext.GetAsync<JsonElement>("/api/v1/contacts/domain");

            var simplifiedContacts = new List<object>();
            if (response.TryGetProperty("success", out var success) && success.GetBoolean() &&
                response.TryGetProperty("contacts", out var contacts))
                foreach (var contact in contacts.EnumerateArray())
                {
                    string? emailList = null;
                    if (contact.TryGetProperty("emailAddressList", out var emails) && emails.GetArrayLength() > 0)
                    {
                        var firstEmail = emails[0];
                        // Handle both string and object formats
                        if (firstEmail.ValueKind == JsonValueKind.String)
                            emailList = firstEmail.GetString();
                        else if (firstEmail.ValueKind == JsonValueKind.Object && 
                                 firstEmail.TryGetProperty("address", out var addr))
                            emailList = addr.GetString();
                    }

                    var simplifiedContact = new
                    {
                        id = contact.TryGetProperty("id", out var id)
                            ? (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString())
                            : null,
                        username = contact.TryGetProperty("username", out var user) ? user.GetString() : null,
                        displayAs = (contact.TryGetProperty("displayAs", out var display) ? display.GetString() : null) ?? 
                                    (contact.TryGetProperty("DisplayAs", out var d2) ? d2.GetString() : null) ??
                                    (contact.TryGetProperty("displayName", out var dn) ? dn.GetString() : null) ??
                                    (contact.TryGetProperty("DisplayName", out var dn2) ? dn2.GetString() : null) ??
                                    (contact.TryGetProperty("fullName", out var fn) ? fn.GetString() : null) ??
                                    (contact.TryGetProperty("FullName", out var fn2) ? fn2.GetString() : null),
                        emailAddress = emailList ?? 
                                       (contact.TryGetProperty("email", out var em) ? em.GetString() : null) ??
                                       (contact.TryGetProperty("Email", out var em2) ? em2.GetString() : null) ??
                                       (contact.TryGetProperty("emailAddress", out var ea) ? ea.GetString() : null) ??
                                       (contact.TryGetProperty("EmailAddress", out var ea2) ? ea2.GetString() : null)
                    };
                    simplifiedContacts.Add(simplifiedContact);
                }

            var result = new
            {
                contacts = simplifiedContacts,
                success = response.TryGetProperty("success", out success) && success.GetBoolean()
            };

            return JsonSerializer.Serialize(result);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Destructive = true)]
    [Description("Delete contacts from the specified folder")]
    public static async Task<string> DeleteContacts(
        [Description("The ID of the contact folder")]
        string folderId,
        [Description("The IDs of the contacts to delete")]
        string contactIds,
        //SmarterMailDatabase db,
        UserContext userContext)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot delete contacts." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            // Extract just the GUID from sourceIdPath (format is "owner/guid")
            var parts = sourceIdPath.Split('/');
            var sourceGuid = parts.Length > 1 ? parts[1] : parts[0];

            var contactIdList = contactIds.Split(',').Select(id => id.Trim()).ToList();
            var inputData = contactIdList.Select(contactId => new
            {
                sourceOwner = userContext.Username,
                sourceId = sourceGuid,
                id = contactId
            }).ToList();

            var response = await userContext.PostAsync<JsonElement>("/api/v1/contacts/delete-bulk", inputData);
            return JsonSerializer.Serialize(response);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Create a new contact in the specified folder")]
    public static async Task<string> CreateContact(
        UserContext userContext,
        [Description("The ID of the contact folder")]
        string folderId,
        [Description("First name of the contact")]
        string firstName = "",
        [Description("Last name of the contact")]
        string lastName = "",
        [Description("Email address of the contact")]
        string email = "",
        [Description("Display name for the contact")]
        string displayAs = "",
        [Description("Company name")]
        string company = "",
        [Description("Job title")]
        string jobTitle = "",
        [Description("Phone number")]
        string phoneNumber = "",
        [Description("Home street address")]
        string homeStreet = "",
        [Description("Home city")]
        string homeCity = "",
        [Description("Home state")]
        string homeState = "",
        [Description("Home zip code")]
        string homeZip = "",
        [Description("Home country")]
        string homeCountry = "",
        [Description("Business street address")]
        string busStreet = "",
        [Description("Business city")]
        string busCity = "",
        [Description("Business state")]
        string busState = "",
        [Description("Business zip code")]
        string busZip = "",
        [Description("Business country")]
        string busCountry = "",
        [Description("Web page URL")]
        string webPage = "",
        [Description("Additional notes about the contact (can be HTML)")]
        string additionalInfo = "",
        [Description("Whether additional info is HTML formatted")]
        bool isHtml = false)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot create contacts." });

        try
        {
            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            // Build the contact data object with PascalCase property names
            var contactData = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(firstName))
                contactData["FirstName"] = firstName;
            if (!string.IsNullOrEmpty(lastName))
                contactData["LastName"] = lastName;

            // DisplayAs is required - auto-generate if not provided
            if (!string.IsNullOrEmpty(displayAs))
                contactData["DisplayAs"] = displayAs;
            else if (!string.IsNullOrEmpty(firstName) || !string.IsNullOrEmpty(lastName))
                contactData["DisplayAs"] = $"{firstName} {lastName}".Trim();
            else if (!string.IsNullOrEmpty(email))
                contactData["DisplayAs"] = email;
            else
                contactData["DisplayAs"] = "New Contact";
            if (!string.IsNullOrEmpty(company))
                contactData["Company"] = company;
            if (!string.IsNullOrEmpty(jobTitle))
                contactData["JobTitle"] = jobTitle;
            if (!string.IsNullOrEmpty(webPage))
                contactData["WebPage"] = webPage;
            if (!string.IsNullOrEmpty(additionalInfo))
            {
                contactData["AdditionalInfo"] = additionalInfo;
                contactData["IsHtml"] = isHtml;
            }

            // Add email address
            if (!string.IsNullOrEmpty(email))
                contactData["EmailAddressList"] = new[] { email };

            // Add phone number
            if (!string.IsNullOrEmpty(phoneNumber))
                contactData["PhoneNumberList"] = new[] { new { Number = phoneNumber, Type = "Home" } };

            // Add home address fields
            if (!string.IsNullOrEmpty(homeStreet))
                contactData["HomeStreet"] = homeStreet;
            if (!string.IsNullOrEmpty(homeCity))
                contactData["HomeCity"] = homeCity;
            if (!string.IsNullOrEmpty(homeState))
                contactData["HomeState"] = homeState;
            if (!string.IsNullOrEmpty(homeZip))
                contactData["HomeZip"] = homeZip;
            if (!string.IsNullOrEmpty(homeCountry))
                contactData["HomeCountry"] = homeCountry;

            // Add business address fields
            if (!string.IsNullOrEmpty(busStreet))
                contactData["BusStreet"] = busStreet;
            if (!string.IsNullOrEmpty(busCity))
                contactData["BusCity"] = busCity;
            if (!string.IsNullOrEmpty(busState))
                contactData["BusState"] = busState;
            if (!string.IsNullOrEmpty(busZip))
                contactData["BusZip"] = busZip;
            if (!string.IsNullOrEmpty(busCountry))
                contactData["BusCountry"] = busCountry;

            // Extract the folder UID from the sourceIdPath
            // sourceIdPath format is "owner/sourceGuid" from GetSourceIdPathFromFolderIdAsync
            var parts = sourceIdPath.Split('/');
            var folderUid = parts.Length > 1 ? parts[1] : parts[0];

            // Make the API call to create the contact
            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/contacts/contact-put/{userContext.Username}/{folderUid}",
                contactData);

            return JsonSerializer.Serialize(response);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Update an existing contact in the specified folder")]
    public static async Task<string> UpdateContact(
        UserContext userContext,
        [Description("The ID of the contact folder")]
        string folderId,
        [Description("The ID of the contact to update")]
        string contactId,
        [Description("First name of the contact")]
        string firstName = "",
        [Description("Last name of the contact")]
        string lastName = "",
        [Description("Email address of the contact")]
        string email = "",
        [Description("Display name for the contact")]
        string displayAs = "",
        [Description("Company name")]
        string company = "",
        [Description("Job title")]
        string jobTitle = "",
        [Description("Phone number")]
        string phoneNumber = "",
        [Description("Home street address")]
        string homeStreet = "",
        [Description("Home city")]
        string homeCity = "",
        [Description("Home state")]
        string homeState = "",
        [Description("Home zip code")]
        string homeZip = "",
        [Description("Home country")]
        string homeCountry = "",
        [Description("Business street address")]
        string busStreet = "",
        [Description("Business city")]
        string busCity = "",
        [Description("Business state")]
        string busState = "",
        [Description("Business zip code")]
        string busZip = "",
        [Description("Business country")]
        string busCountry = "",
        [Description("Web page URL")]
        string webPage = "",
        [Description("Additional notes about the contact (can be HTML)")]
        string additionalInfo = "",
        [Description("Whether additional info is HTML formatted")]
        bool isHtml = false)
    {
        if (userContext.ReadOnlyMode)
            return JsonSerializer.Serialize(new
                { success = false, error = "Read-only mode is enabled. Cannot update contacts." });

        try
        {
            if (string.IsNullOrEmpty(contactId))
                return JsonSerializer.Serialize(new { success = false, error = "Contact ID is required for updates" });

            var sourceIdPath =
                await userContext.GetSourceIdPathFromFolderIdAsync(folderId, userContext.Username, userContext.Domain);
            if (sourceIdPath == null)
                return JsonSerializer.Serialize(new { success = false, error = $"No folder found for '{folderId}'. Use list_folder_info_by_type with type 3 to see available contact folders." });

            // Build the contact data object with the ID for update
            var contactData = new Dictionary<string, object>
            {
                ["Id"] = contactId
            };

            if (!string.IsNullOrEmpty(firstName))
                contactData["FirstName"] = firstName;
            if (!string.IsNullOrEmpty(lastName))
                contactData["LastName"] = lastName;

            // DisplayAs is required
            if (!string.IsNullOrEmpty(displayAs))
                contactData["DisplayAs"] = displayAs;
            else if (!string.IsNullOrEmpty(firstName) || !string.IsNullOrEmpty(lastName))
                contactData["DisplayAs"] = $"{firstName} {lastName}".Trim();
            else if (!string.IsNullOrEmpty(email))
                contactData["DisplayAs"] = email;

            if (!string.IsNullOrEmpty(company))
                contactData["Company"] = company;
            if (!string.IsNullOrEmpty(jobTitle))
                contactData["JobTitle"] = jobTitle;
            if (!string.IsNullOrEmpty(webPage))
                contactData["WebPage"] = webPage;
            if (!string.IsNullOrEmpty(additionalInfo))
            {
                contactData["AdditionalInfo"] = additionalInfo;
                contactData["IsHtml"] = isHtml;
            }

            if (!string.IsNullOrEmpty(email))
                contactData["EmailAddressList"] = new[] { email };

            if (!string.IsNullOrEmpty(phoneNumber))
                contactData["PhoneNumberList"] = new[] { new { Number = phoneNumber, PhoneType = 0 } };

            if (!string.IsNullOrEmpty(homeStreet))
                contactData["HomeStreet"] = homeStreet;
            if (!string.IsNullOrEmpty(homeCity))
                contactData["HomeCity"] = homeCity;
            if (!string.IsNullOrEmpty(homeState))
                contactData["HomeState"] = homeState;
            if (!string.IsNullOrEmpty(homeZip))
                contactData["HomeZip"] = homeZip;
            if (!string.IsNullOrEmpty(homeCountry))
                contactData["HomeCountry"] = homeCountry;

            if (!string.IsNullOrEmpty(busStreet))
                contactData["BusStreet"] = busStreet;
            if (!string.IsNullOrEmpty(busCity))
                contactData["BusCity"] = busCity;
            if (!string.IsNullOrEmpty(busState))
                contactData["BusState"] = busState;
            if (!string.IsNullOrEmpty(busZip))
                contactData["BusZip"] = busZip;
            if (!string.IsNullOrEmpty(busCountry))
                contactData["BusCountry"] = busCountry;

            var parts = sourceIdPath.Split('/');
            var folderUid = parts.Length > 1 ? parts[1] : parts[0];

            var response = await userContext.PostAsync<JsonElement>(
                $"/api/v1/contacts/contact-put/{userContext.Username}/{folderUid}",
                contactData);

            return JsonSerializer.Serialize(response);
        }
        catch (SmarterMailApiException apiEx)
        {
            return JsonSerializer.Serialize(new { success = false, error = apiEx.Message, statusCode = (int)apiEx.StatusCode, apiError = apiEx.ResponseBody, endpoint = apiEx.Endpoint });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }
}