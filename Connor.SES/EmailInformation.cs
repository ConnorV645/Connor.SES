using Amazon.SimpleEmail.Model;

namespace Connor.SES
{
    public class EmailInformation<Y> where Y : SendEmailRequest
    {
        public SendEmailResponse Response { get; set; }
        public Y Request { get; set; }
    }
}
