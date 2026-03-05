namespace App.MinimalApi.Model.Models
{
    public class BaseResponseDto<T>
    {
        public short? Code { get; set; }
        public T? data { get; set; }
        public string? status { get; set; }
        public string? Message { get; set; }
    }

    public class BaseResponseDto : BaseResponseDto<object>
    {
        public static BaseResponseDto ErrorResponse(short code)
        {
            return new BaseResponseDto
            {
                Code = code,
                data = null,
                status = "error"
            };
        }

        public static BaseResponseDto BadRequestResponse(short code)
        {
            return new BaseResponseDto
            {
                Code = code,
                data = null,
                status = "error"
            };
        }

        public static BaseResponseDto CreateSuccessResponse<T>(T data)
        {
            return new BaseResponseDto
            {
                Code = 200,
                data = data,
                status = "success"
            };
        }
    }
}