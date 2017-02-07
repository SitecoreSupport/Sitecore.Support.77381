using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Sitecore.ExperienceEditor.Speak.Server.Responses;
using Sitecore.Data;
using Sitecore.Data.Items;
using Sitecore.Data.Fields;

namespace Sitecore.Support.ExperienceEditor.Speak.Ribbon.Requests.SaveItem
{
    public class CallServerSavePipeline: Sitecore.ExperienceEditor.Speak.Ribbon.Requests.SaveItem.CallServerSavePipeline
    {
        public override PipelineProcessorResponseValue ProcessRequest()
        {
            this.FixValues(base.RequestContext.Item.Database, base.RequestContext.FieldValues);
            return base.ProcessRequest();
        }

        private void FixValues(Database database, Dictionary<string, string> dictionaryForm)
        {
            string[] array = dictionaryForm.Keys.ToArray<string>();
            string[] array2 = array;
            for (int i = 0; i < array2.Length; i++)
            {
                string text = array2[i];
                if (text.StartsWith("fld_", StringComparison.InvariantCulture) || text.StartsWith("flds_", StringComparison.InvariantCulture))
                {
                    string text2 = text;
                    int num = text2.IndexOf('$');
                    if (num >= 0)
                    {
                        text2 = StringUtil.Left(text2, num);
                    }
                    string[] array3 = text2.Split(new char[]
                    {
                '_'
                    });
                    ID iD = ShortID.DecodeID(array3[1]);
                    ID iD2 = ShortID.DecodeID(array3[2]);
                    Item item = database.GetItem(iD);
                    if (item != null)
                    {
                        Field field = item.Fields[iD2];
                        string typeKey = field.TypeKey;
                        if (typeKey != null && typeKey.Equals("single-line text", StringComparison.InvariantCultureIgnoreCase))
                        {
                            dictionaryForm[text] = StringUtil.RemoveTags(dictionaryForm[text]);
                        }
                    }
                }
            }
        }
    }
}