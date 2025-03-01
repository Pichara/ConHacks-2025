using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Health_System.Controllers
{
    public class HomeController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;

        // IMPORTANT: Store your API key securely (for example, in configuration or environment variables)
        private const string OpenAiApiKey = "";

        public HomeController(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        // GET: Home/Index
        public IActionResult Index()
        {
            return View();
        }

        // POST: Home/AnalyzeTest
        [HttpPost]
        public async Task<IActionResult> AnalyzeTest()
        {
            // Ensure a lab report image is uploaded.
            if (Request.Form.Files.Count == 0)
            {
                ViewBag.Error = "Please upload a lab report image.";
                return View("Index");
            }

            // Retrieve the user's personal details.
            string age = Request.Form["Age"];
            string weight = Request.Form["Weight"];
            string height = Request.Form["Height"];
            string allergies = Request.Form["Allergies"];
            string medicalConditions = Request.Form["MedicalConditions"];

            // Retrieve the selected analysis options.
            var selectedOptions = Request.Form["Options"];
            if (selectedOptions.Count == 0)
            {
                ViewBag.Error = "Please select at least one analysis option.";
                return View("Index");
            }

            // Convert the uploaded lab report image to a Base64 string.
            var file = Request.Form.Files[0];
            string base64Image = string.Empty;
            if (file.Length > 0)
            {
                using (var ms = new MemoryStream())
                {
                    await file.CopyToAsync(ms);
                    byte[] imageBytes = ms.ToArray();
                    base64Image = Convert.ToBase64String(imageBytes);
                }
            }

            // Create tasks to call the OpenAI API concurrently for each selected option.
            var tasks = new List<Task<KeyValuePair<string, string>>>();
            foreach (var option in selectedOptions)
            {
                // Build a detailed prompt for this analysis option.
                string prompt = BuildPrompt(option, age, weight, height, allergies, medicalConditions);
                prompt = prompt + " do not use ```html, do not write patient infromation do not write ``` and put complete and great deatiles and information do your best";
                tasks.Add(ProcessAnalysisOptionAsync(option, prompt, base64Image));
            }

            // Wait for all API calls to complete.
            var responses = await Task.WhenAll(tasks);
            var results = responses.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Pass the results to the Report view.
            return View("Report", results);
        }

        /// <summary>
        /// Processes an individual analysis option by calling the OpenAI API with a tailored prompt.
        /// </summary>
        private async Task<KeyValuePair<string, string>> ProcessAnalysisOptionAsync(string option, string prompt, string base64Image)
        {
            string responseText = await CallOpenAiApiAsync(prompt, base64Image);
            return new KeyValuePair<string, string>(option, responseText);
        }

        /// <summary>
        /// Calls the OpenAI API with the provided prompt and attached lab report image.
        /// </summary>
        private async Task<string> CallOpenAiApiAsync(string prompt, string base64Image)
        {
            // Build the request body for the API.
            var requestBody = new
            {
                model = "gpt-4o-mini",
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + base64Image } }
                        }
                    }
                }
            };

            string json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Create HttpClient and set the Authorization header.
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + OpenAiApiKey);

            var apiResponse = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);
            string responseString = await apiResponse.Content.ReadAsStringAsync();

            // Extract the message content from the API response.
            string chatContent = string.Empty;
            using (JsonDocument document = JsonDocument.Parse(responseString))
            {
                if (document.RootElement.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                {
                    var message = choices[0].GetProperty("message");
                    if (message.TryGetProperty("content", out JsonElement contentElement))
                    {
                        chatContent = contentElement.GetString();
                    }
                }
            }
            return chatContent;
        }

        /// <summary>
        /// Builds a detailed and comprehensive prompt based on the selected analysis option and the user’s details.
        /// </summary>
        private string BuildPrompt(string option, string age, string weight, string height, string allergies, string medicalConditions)
        {
            // Create a summary of the user's details.
            string userDetails = $"Age: {age}, Weight: {weight} kg, Height: {height} cm, Allergies: {(string.IsNullOrWhiteSpace(allergies) ? "None" : allergies)}";
            if (!string.IsNullOrWhiteSpace(medicalConditions))
            {
                userDetails += $", Medical Conditions: {medicalConditions}";
            }

            // Build a long, detailed prompt for each analysis type.
            switch (option)
            {
                case "nutrition":
                    return $"You are an expert nutritionist with extensive experience in biochemical and metabolic health analysis. The user provided these details: {userDetails}. They have uploaded a lab report image containing vital nutritional markers such as vitamin levels, mineral balances, and metabolic indicators. Please provide a thorough and comprehensive nutritional analysis. Your answer should include identification of any deficiencies, explanations of abnormal values, and tailored dietary recommendations with specific food and supplement suggestions. Format your answer as an HTML snippet with inline CSS styling (using clear headings and paragraphs) without extra HTML boilerplate.";
                case "fitness":
                    return $"You are a top-tier fitness coach and physiologist. The user details are: {userDetails}. Along with these details, they have provided a lab report image that includes key indicators of muscle health, cardiovascular performance, and metabolic function. Develop a comprehensive, personalized fitness plan that covers exercise routines, strength and endurance training, and risk management strategies. Provide detailed explanations for each recommendation and format your response as an HTML snippet with inline CSS (using headings and paragraphs) to ensure clarity.";
                case "details":
                    return $"You are a seasoned medical doctor specializing in laboratory diagnostics. The user’s details are as follows: {userDetails}. They have uploaded a lab report image with multiple diagnostic parameters. Conduct an in-depth analysis of every section of the report, explaining the significance of each measurement, what constitutes normal versus abnormal values, and any potential clinical implications. Present your analysis in a structured format using an HTML snippet with inline CSS styling (headings, paragraphs) to make the content clear and professional.";
                case "summary":
                    return $"You are an experienced clinician known for your ability to succinctly summarize complex lab reports. The user’s information is: {userDetails}. With the provided lab report image, produce a concise yet informative summary that highlights key findings, pinpoints any abnormal values, and gives an overall health status assessment. Your summary should be formatted as an HTML snippet with inline CSS styling (using headings and paragraphs) for easy reading.";
                case "generalAdvice":
                    return $"You are a trusted health advisor with expertise in holistic wellness. The user’s details: {userDetails} are provided along with their lab report image. Offer broad, personalized health insights and lifestyle recommendations based on the lab data. Explain how the lab findings relate to overall health and suggest actionable tips for improving well-being. Ensure your response is detailed, using an HTML snippet with inline CSS styling (headings and paragraphs) for clarity and professional presentation.";
                case "furtherTesting":
                    return $"You are an expert in medical diagnostics and further clinical evaluation. The user provided these details: {userDetails} along with a lab report image. Based on a thorough review of the lab data, suggest additional diagnostic tests or follow-up evaluations that could help achieve a more complete understanding of the user's health status. Include detailed reasoning behind each recommendation and format your answer as an HTML snippet with inline CSS styling (using clear headings and paragraphs).";
                default:
                    return $"Please analyze the provided lab report image along with the user's details: {userDetails}. Deliver a detailed, professional analysis with clear recommendations. Format your answer as an HTML snippet with inline CSS styling using headings and paragraphs.";
            }
        }
    }
}
