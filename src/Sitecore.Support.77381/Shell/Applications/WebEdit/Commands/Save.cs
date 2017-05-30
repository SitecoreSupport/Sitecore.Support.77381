using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.UI;
using Sitecore.Collections;
using Sitecore.Configuration;
using Sitecore.Data;
using Sitecore.Data.Fields;
using Sitecore.Data.Items;
using Sitecore.Data.Validators;
using Sitecore.Diagnostics;
using Sitecore.Globalization;
using Sitecore.Pipelines;
using Sitecore.Pipelines.GetPageEditorValidators;
using Sitecore.Pipelines.Save;
using Sitecore.Shell.Applications.WebEdit.Commands;
using Sitecore.Shell.Framework.Commands;
using Sitecore.Web;
using Sitecore.Web.UI.Sheer;
using Sitecore.Xml;
using ValidatorCollection = Sitecore.Data.Validators.ValidatorCollection;

namespace Sitecore.Support.Shell.Applications.WebEdit.Commands
{
  [Serializable]
  public class Save : WebEditCommand
  {
    private static readonly string FirefoxItemLinkPrefix = "~/link.aspx?".Replace("~", "%7E");

    private static FieldDescriptor AddField(Packet packet, PageEditorField pageEditorField)
    {
      Assert.ArgumentNotNull(packet, "packet");
      Assert.ArgumentNotNull(pageEditorField, "pageEditorField");

      var item = Client.ContentDatabase.GetItem(pageEditorField.ItemID, pageEditorField.Language, pageEditorField.Version);

      if (item == null) return null;

      var field = item.Fields[pageEditorField.FieldID];
      var s = pageEditorField.Value;

      switch (field.TypeKey)
      {
        case "html":
        case "rich text":
          s = RepairLinks(s.TrimEnd(' '));
          break;

        case "text":
        case "single-line text":

          #region Patch changes

          s = StringUtil.RemoveTags(s);

          #endregion Patch changes

          s = HttpUtility.HtmlDecode(s);
          break;

        case "integer":
        case "number":
          s = StringUtil.RemoveTags(s);
          break;

        case "multi-line text":
        case "memo":
          s =
            StringUtil.RemoveTags(
              s.Replace("<br>", "\r\n")
                .Replace("<br/>", "\r\n")
                .Replace("<br />", "\r\n")
                .Replace("<BR>", "\r\n")
                .Replace("<BR/>", "\r\n")
                .Replace("<BR />", "\r\n"));
          break;

        case "word document":
          s = string.Join(Environment.NewLine, s.Split(new[] { "\r\n", "\n\r", "\n" }, StringSplitOptions.None));
          break;
      }
      var fieldValidationErrorMessage = GetFieldValidationErrorMessage(field, s);

      if (fieldValidationErrorMessage != string.Empty) throw new FieldValidationException(fieldValidationErrorMessage);

      if (s == field.Value)
      {
        var fieldRegexValidationError = FieldUtil.GetFieldRegexValidationError(field, s);

        if (!string.IsNullOrEmpty(fieldRegexValidationError) && !item.Paths.IsMasterPart && !StandardValuesManager.IsStandardValuesHolder(item)) throw new FieldValidationException(fieldRegexValidationError);
        return new FieldDescriptor(item.Uri, field.ID, s, field.ContainsStandardValue);
      }
      var node = packet.XmlDocument.SelectSingleNode(string.Concat("/*/field[@itemid='", pageEditorField.ItemID, "' and @language='", pageEditorField.Language, "' and @version='", pageEditorField.Version, "' and @fieldid='", pageEditorField.FieldID, "']"));

      if (node != null)
      {
        var item2 = Client.ContentDatabase.GetItem(pageEditorField.ItemID, pageEditorField.Language, pageEditorField.Version);

        if (item2 == null) return null;
        if (s == item2[pageEditorField.FieldID]) return new FieldDescriptor(item.Uri, field.ID, s, false);
        if (s != null) node.ChildNodes[0].InnerText = s;
      }
      else
      {
        packet.StartElement("field");
        packet.SetAttribute("itemid", pageEditorField.ItemID.ToString());
        packet.SetAttribute("language", pageEditorField.Language.ToString());
        packet.SetAttribute("version", pageEditorField.Version.ToString());
        packet.SetAttribute("fieldid", pageEditorField.FieldID.ToString());
        packet.SetAttribute("itemrevision", pageEditorField.Revision);
        packet.AddElement("value", s);
        packet.EndElement();
      }
      return new FieldDescriptor(item.Uri, field.ID, s, false);
    }

    private static void AddLayoutField(Page page, Packet packet, Item item)
    {
      Assert.ArgumentNotNull(page, "page");
      Assert.ArgumentNotNull(packet, "packet");
      Assert.ArgumentNotNull(item, "item");
      var delta = page.Request.Form["scLayout"];

      if (string.IsNullOrEmpty(delta)) return;
      delta = WebEditUtil.ConvertJSONLayoutToXML(delta);
      Assert.IsNotNull(delta, delta);

      if (item.Name != "__Standard Values") delta = XmlDeltas.GetDelta(delta, item.Fields[FieldIDs.LayoutField].GetStandardValue());

      packet.StartElement("field");
      packet.SetAttribute("itemid", item.ID.ToString());
      packet.SetAttribute("language", item.Language.ToString());
      packet.SetAttribute("version", item.Version.ToString());
      packet.SetAttribute("fieldid", FieldIDs.LayoutField.ToString());
      packet.AddElement("value", delta);
      packet.EndElement();
    }

    private static Packet CreatePacket(IEnumerable<PageEditorField> fields,
      out SafeDictionary<FieldDescriptor, string> controlsToValidate)
    {
      Assert.ArgumentNotNull(fields, "fields");

      var packet = new Packet();
      controlsToValidate = new SafeDictionary<FieldDescriptor, string>();

      foreach (var field in fields)
      {
        var descriptor = AddField(packet, field);

        if (descriptor == null) continue;

        var str = field.ControlId ?? string.Empty;
        controlsToValidate[descriptor] = str;

        if (!string.IsNullOrEmpty(str)) RuntimeValidationValues.Current[str] = descriptor.Value;
      }
      return packet;
    }

    public override void Execute(CommandContext context)
    {
      Assert.ArgumentNotNull(context, "context");

      var current = HttpContext.Current;
      var handler = current?.Handler as Page;

      if (handler == null || context.Items.Length != 1) return;

      SafeDictionary<FieldDescriptor, string> dictionary;
      Packet packet;
      ValidatorsMode mode;
      var fields = GetFields(handler.Request.Form);

      try
      {
        packet = CreatePacket(fields, out dictionary);
      }
      catch (FieldValidationException exception)
      {
        SheerResponse.Alert(exception.Message);
        return;
      }

      var item = context.Items[0];
      if (WebEditUtil.CanDesignItem(item)) AddLayoutField(handler, packet, item);

      var validators = GetValidators(item, dictionary, out mode);
      var formValue = WebUtil.GetFormValue("scValidatorsKey");

      if (!string.IsNullOrEmpty(formValue))
      {
        validators.Key = formValue;
        ValidatorManager.SetValidators(mode, formValue, validators);
      }

      var pipeline = PipelineFactory.GetPipeline("saveUI");
      pipeline.ID = ShortID.Encode(ID.NewID);

      var args = new SaveArgs(packet.XmlDocument)
      {
        SaveAnimation = false,
        PostAction = StringUtil.GetString(context.Parameters["postaction"]),
        PolicyBasedLocking = true
      };
      args.CustomData["showvalidationdetails"] = true;
      SheerResponse.SetPipeline(pipeline.ID);
      pipeline.Start(args);

      if (!string.IsNullOrEmpty(args.Error))
        SheerResponse.Alert(args.Error);
    }

    private static string GetFieldValidationErrorMessage(Field field, string value)
    {
      Assert.ArgumentNotNull(field, "field");
      Assert.ArgumentNotNull(value, "value");

      if (!Settings.WebEdit.ValidationEnabled) return string.Empty;
      var cultureInfo = LanguageUtil.GetCultureInfo();

      if (value.Length == 0) return string.Empty;

      switch (field.TypeKey)
      {
        case "integer":
          long num;
          return long.TryParse(value, NumberStyles.Integer, cultureInfo, out num) ? string.Empty : Translate.Text("\"{0}\" is not a valid integer.", value);

        case "number":
          double num2;
          return double.TryParse(value, NumberStyles.Float, cultureInfo, out num2) ? string.Empty : Translate.Text("\"{0}\" is not a valid number.", value);
      }
      return string.Empty;
    }

    private ValidatorCollection GetValidators(Item item, SafeDictionary<FieldDescriptor, string> controlsToValidate, out ValidatorsMode mode)
    {
      Assert.ArgumentNotNull(item, "item");
      Assert.ArgumentNotNull(controlsToValidate, "controlsToValidate");

      var fields = new List<FieldDescriptor>(controlsToValidate.Keys);
      var args = new GetPageEditorValidatorsArgs(item, fields);

      try
      {
        CorePipeline.Run("getPageEditorValidators", args);
        var validators = args.Validators;

        foreach (BaseValidator validator in validators)
        {
          var itemUri = validator.ItemUri;

          if (itemUri == null) continue;
          var fieldID = validator.FieldID;

          if (ItemUtil.IsNull(fieldID)) continue;
          var descriptor = fields.FirstOrDefault(f => f.ItemUri == itemUri && f.FieldID == fieldID);

          if (descriptor == null) continue;
          var str = controlsToValidate[descriptor];

          if (!string.IsNullOrEmpty(str)) validator.ControlToValidate = str;
        }
        mode = args.Mode;
        return validators;
      }
      catch (Exception exception)
      {
        Log.Error($"Failed to retrieve validators for {item.DisplayName} item", exception, this);
        mode = args.Mode;
        return new ValidatorCollection();
      }
    }

    public override CommandState QueryState(CommandContext context)
    {
      Assert.ArgumentNotNull(context, "context");
      if (context.Items.Length != 1) return CommandState.Hidden;
      if (context.Items[0] == null) return CommandState.Hidden;
      if (IsWebEditEditingDisabled) return CommandState.Disabled;
      return WebUtil.GetQueryString("sc_ce") == "1" ? CommandState.Disabled : base.QueryState(context);
    }

    public static string RepairLinks(string value)
    {
      Assert.ArgumentNotNull(value, "value");

      if (UIUtil.IsFirefox())
      {
        var oldValue = Settings.Media.DefaultMediaPrefix.Replace("~", "%7E");
        value = value.Replace(FirefoxItemLinkPrefix, "~/link.aspx?")
          .Replace(oldValue, Settings.Media.DefaultMediaPrefix);
      }
      var url = HttpContext.Current.Request.Url;
      var length = url.AbsolutePath.LastIndexOf('/');

      if (length < 0) return value;

      var serverUrl = WebUtil.GetServerUrl(url, false);
      var currentPath = url.AbsolutePath.Substring(0, length);

      value = new Regex("<[^>]+?=\"(?<serverurl>http(s?)\\://[a-z0-9.-]+(:[0-9]+)?)(.+?)\\\"",
          RegexOptions.Compiled | RegexOptions.IgnoreCase).Replace(value, delegate (Match match)
        {
          var str = match.Groups["serverurl"].Value;
          var str2 = match.Value;

          if (!str.StartsWith(serverUrl, StringComparison.OrdinalIgnoreCase)) return str2.Replace(currentPath, string.Empty);

          var startIndex = match.Value.IndexOf(serverUrl, StringComparison.OrdinalIgnoreCase);
          str2 = match.Value.Remove(startIndex, str.Length);

          return str2.Replace(currentPath, string.Empty);
        });

      var list = new List<string>
      {
        "~/link.aspx?",
        "~/media/"
      };

      foreach (var str2 in list)
      {
        var localPrefix = str2;
        var escapedPrefix = Regex.Escape(localPrefix);
        value = new Regex($"(<[^>]+?= /{escapedPrefix})", RegexOptions.Compiled | RegexOptions.IgnoreCase).Replace(value, match => Regex.Replace(match.Value, " / " + escapedPrefix, localPrefix));
      }
      return value;
    }

    private class FieldValidationException : Exception
    {
      public FieldValidationException(string validationError) : base(validationError)
      {
        Assert.ArgumentNotNull(validationError, "validationError");
      }
    }
  }
}